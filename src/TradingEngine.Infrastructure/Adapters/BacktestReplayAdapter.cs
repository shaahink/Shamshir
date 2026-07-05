using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain;
using TradingEngine.Engine;
using TradingEngine.Services.Helpers;

namespace TradingEngine.Infrastructure.Adapters;

public sealed class BacktestReplayAdapter : IBrokerAdapter, IReplayVenue, IAsyncDisposable
{
    private readonly IBarRepository _barRepo;
    private readonly Symbol _symbol;
    private readonly Timeframe _timeframe;
    private readonly DateTime _from;
    private readonly DateTime _to;
    private readonly decimal _initialBalance;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly Func<string, string, decimal> _crossRateProvider;
    private readonly ILogger<BacktestReplayAdapter> _logger;

    private readonly Channel<Tick> _tickChannel =
        Channel.CreateUnbounded<Tick>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly Channel<Bar> _barChannel =
        Channel.CreateUnbounded<Bar>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly Channel<AccountUpdate> _accountChannel =
        Channel.CreateBounded<AccountUpdate>(new BoundedChannelOptions(500)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleWriter = true });
    private readonly Channel<ExecutionEvent> _executionChannel =
        Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1_000)
        { FullMode = BoundedChannelFullMode.Wait, SingleWriter = true });

    private readonly Dictionary<Guid, OpenTrade> _openTrades = new();
    private readonly Dictionary<Guid, PendingLimit> _pendingLimits = new();
    private readonly Dictionary<Guid, PendingStop> _pendingStops = new();
    private decimal _balance;
    private decimal _lastClose;
    private Task _feedTask = Task.CompletedTask;
    private CancellationTokenSource? _feedCts;

    private sealed record OpenTrade(
        TradeDirection Direction,
        decimal EntryPrice,
        decimal Lots,
        DateTime OpenedAtUtc,
        Price StopLoss,
        Price? TakeProfit);
    private sealed class PendingLimit
    {
        public required TradeDirection Direction { get; init; }
        public required decimal Lots { get; init; }
        public required decimal LimitPrice { get; init; }
        public required Price StopLoss { get; init; }
        public Price? TakeProfit { get; init; }
        public int BarsRemaining { get; set; }
    }

    // P2.7: a resting STOP entry order — the mirror image of PendingLimit. A buy stop fills when price
    // rises UP THROUGH the trigger (breakout confirmation); a sell stop fills when price falls DOWN
    // THROUGH it. Same expiry semantics as a limit (BarsRemaining from LimitOrderExpiryBars).
    private sealed class PendingStop
    {
        public required TradeDirection Direction { get; init; }
        public required decimal Lots { get; init; }
        public required decimal StopPrice { get; init; }
        public required Price StopLoss { get; init; }
        public Price? TakeProfit { get; init; }
        public int BarsRemaining { get; set; }
    }

    public bool IsConnected { get; private set; }
    public ChannelReader<Tick> TickStream => _tickChannel.Reader;
    public ChannelReader<Bar> BarStream => _barChannel.Reader;
    public ChannelReader<AccountUpdate> AccountStream => _accountChannel.Reader;
    public ChannelReader<ExecutionEvent> ExecutionStream => _executionChannel.Reader;
    public int BarCount { get; private set; }
    public DateTime BrokerTimeUtc { get; private set; }

    // iter-redesign-ctrader P1.4: the replay adapter owns exit detection (same model as cTrader).
    // The engine no longer runs DetectSlTpExit for any venue — the adapter detects SL/TP hits against
    // each bar's OHLC and emits reasoned close execution events, exactly like cTrader does.
    public ExitMode ExitMode => ExitMode.VenueManaged;
    public IReadOnlySet<Guid> GetOpenPositionIds() => _openTrades.Keys.ToHashSet();

    public BacktestReplayAdapter(
        IBarRepository barRepo,
        Symbol symbol,
        Timeframe timeframe,
        DateTime from,
        DateTime to,
        decimal initialBalance,
        ISymbolInfoRegistry symbolRegistry,
        Func<string, string, decimal> crossRateProvider,
        ILogger<BacktestReplayAdapter> logger)
    {
        _barRepo = barRepo;
        _symbol = symbol;
        _timeframe = timeframe;
        _from = from;
        _to = to;
        _initialBalance = initialBalance;
        _balance = initialBalance;
        _symbolRegistry = symbolRegistry;
        _crossRateProvider = crossRateProvider;
        _logger = logger;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        IsConnected = true;
        _feedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        await _accountChannel.Writer.WriteAsync(
            new AccountUpdate(_initialBalance, _initialBalance, 0, _from), ct);

        _feedTask = FeedBarsAsync(_feedCts.Token);
    }

    private async Task FeedBarsAsync(CancellationToken ct)
    {
        try
        {
            var bars = await _barRepo.GetAsync(_symbol, _timeframe, _from, _to, ct);
            BarCount = bars.Count;
            _logger.LogInformation("BacktestReplay: loaded {Count} bars for {Symbol} {Tf}",
                bars.Count, _symbol, _timeframe);

            if (bars.Count == 0)
            {
                _logger.LogWarning("BacktestReplay: no bars found for {Symbol} {Tf} in [{From}–{To}]. Check seed-bars.ps1.",
                    _symbol, _timeframe, _from, _to);
            }

            foreach (var bar in bars)
            {
                ct.ThrowIfCancellationRequested();

                // F1 (iter-26): the feed only produces bars+ticks. It must NOT compute equity here —
                // it races ahead of execution on a different thread, so floating PnL would be measured
                // against an empty/stale open book and `_openTrades` would be read while the engine
                // thread mutates it. Equity/account updates are emitted on the ENGINE thread instead,
                // in SyncToBar (per-bar mark-to-market) and on each realized close.
                await _barChannel.Writer.WriteAsync(bar, ct);
                await _tickChannel.Writer.WriteAsync(
                    new Tick(bar.Symbol, bar.Close, SpreadConvention.AskPrice(bar.Close, GetSpread()), bar.OpenTimeUtc), ct);
            }

            _logger.LogInformation("BacktestReplay: feed complete, {Count} bars sent", bars.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("BacktestReplay: feed cancelled");
        }
        finally
        {
            // Only the bar/tick streams end with the feed. The account stream stays open so the
            // engine-thread emitters (SyncToBar / close) can keep publishing realized equity; it is
            // completed in DisconnectAsync/DisposeAsync.
            _barChannel.Writer.TryComplete();
            _tickChannel.Writer.TryComplete();
        }
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        IsConnected = false;
        _feedCts?.Cancel();
        _barChannel.Writer.TryComplete();
        _tickChannel.Writer.TryComplete();
        _accountChannel.Writer.TryComplete();
        _executionChannel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public Task<AccountState> GetAccountStateAsync(CancellationToken ct)
        => Task.FromResult(new AccountState(_balance, _balance, []));

    public Task<Guid> SubmitOrderAsync(OrderRequest request, CancellationToken ct)
    {
        // Use the engine's order id (= kernel PositionId) when supplied, so the venue's open-trade book,
        // execution events and close all key off the SAME id the kernel uses (iter-36 K2). Falls back to a
        // fresh id for the legacy imperative path (which captures the returned id).
        var orderId = request.ClientOrderId ?? Guid.NewGuid();

        var sl = request.Intent.StopLoss;
        var tp = request.Intent.TakeProfit;

        // Resting limit order: hold it until a later bar's range reaches the limit price, or until it
        // expires. Market orders (and limits with no price) fill instantly at the last close.
        if (request.Type == OrderType.Limit && request.LimitPrice is { } limit)
        {
            _pendingLimits[orderId] = new PendingLimit
            {
                Direction = request.Direction,
                Lots = request.Lots,
                LimitPrice = limit.Value,
                StopLoss = sl,
                TakeProfit = tp,
                BarsRemaining = request.Intent.Entry?.LimitOrderExpiryBars ?? 3,
            };
            _logger.LogDebug("BacktestReplay: limit rest {Id} at {Price:F5} dir={Dir} lots={Lots} expiry={Bars}b",
                orderId, limit.Value, request.Direction, request.Lots, _pendingLimits[orderId].BarsRemaining);
            return Task.FromResult(orderId);
        }

        // P2.7: resting STOP entry order — reuses the same LimitPrice field as the generic "resting
        // trigger price" (OrderType tags which semantics apply). Held until a later bar's range crosses
        // the trigger, or until it expires.
        if (request.Type == OrderType.Stop && request.LimitPrice is { } stop)
        {
            _pendingStops[orderId] = new PendingStop
            {
                Direction = request.Direction,
                Lots = request.Lots,
                StopPrice = stop.Value,
                StopLoss = sl,
                TakeProfit = tp,
                BarsRemaining = request.Intent.Entry?.LimitOrderExpiryBars ?? 3,
            };
            _logger.LogDebug("BacktestReplay: stop rest {Id} at {Price:F5} dir={Dir} lots={Lots} expiry={Bars}b",
                orderId, stop.Value, request.Direction, request.Lots, _pendingStops[orderId].BarsRemaining);
            return Task.FromResult(orderId);
        }

        var midPrice = _lastClose > 0 ? _lastClose : 1m;
        var spread = GetSpread();
        var fillPrice = new Price(request.Direction == TradeDirection.Long ? SpreadConvention.AskPrice(midPrice, spread) : midPrice);
        FillEntry(orderId, request.Direction, fillPrice.Value, request.Lots, sl, tp);
        _logger.LogDebug("BacktestReplay: instant fill {Id} at {Price:F5} dir={Dir} lots={Lots}",
            orderId, fillPrice.Value, request.Direction, request.Lots);
        return Task.FromResult(orderId);
    }

    private void EmitExecutionEvent(ExecutionEvent evt)
    {
        if (!_executionChannel.Writer.TryWrite(evt))
            _logger.LogError("BacktestReplay: execution channel full — event dropped; orderId={OrderId}", evt.OrderId);
    }

    // P0.2 (D3): FULL spread — bars are bid, ask = bid + spread. See SpreadConvention.
    private decimal GetSpread()
    {
        try { return _symbolRegistry.Get(_symbol).TypicalSpread; }
        catch { return 0.0001m; }
    }

    private void FillEntry(Guid orderId, TradeDirection direction, decimal fillPrice, decimal lots, Price sl, Price? tp)
    {
        EmitExecutionEvent(
            new ExecutionEvent(orderId, OrderState.Filled, new Price(fillPrice), lots, null, BrokerTimeUtc) { Symbol = _symbol });
        _openTrades[orderId] = new OpenTrade(direction, fillPrice, lots, BrokerTimeUtc, sl, tp);
    }

    // Match resting limit orders against the bar that just became current. A buy limit fills when the
    // bar trades down to (or below) the limit; a sell limit when it trades up to it. Fill is at the
    // limit price (no slippage). Orders that don't fill burn one bar of life and, once expired, emit a
    // cancellation carrying ENTRY_EXPIRED so the engine journals the expiry instead of a phantom fill.
    private void ProcessPendingLimits(Bar bar)
    {
        if (_pendingLimits.Count == 0) return;

        var spread = GetSpread();

        foreach (var (orderId, limit) in _pendingLimits.ToList())
        {
            // Buy limit: fills once the ASK (bid-low + spread) reaches it. Sell limit: fills once the
            // raw bid-high reaches it (a sell-to-open trades at bid — no spread adjustment).
            var reached = limit.Direction == TradeDirection.Long
                ? SpreadConvention.AskPrice(bar.Low, spread) <= limit.LimitPrice
                : bar.High >= limit.LimitPrice;

            if (reached)
            {
                _pendingLimits.Remove(orderId);
                FillEntry(orderId, limit.Direction, limit.LimitPrice, limit.Lots, limit.StopLoss, limit.TakeProfit);
                _logger.LogDebug("BacktestReplay: limit fill {Id} at {Price:F5} dir={Dir}",
                    orderId, limit.LimitPrice, limit.Direction);
                continue;
            }

            limit.BarsRemaining--;
            if (limit.BarsRemaining <= 0)
            {
                _pendingLimits.Remove(orderId);
                EmitExecutionEvent(new ExecutionEvent(
                    orderId, OrderState.Cancelled, null, 0, "ENTRY_EXPIRED", BrokerTimeUtc) { Symbol = _symbol });
                _logger.LogDebug("BacktestReplay: limit expired {Id} at {Price:F5}", orderId, limit.LimitPrice);
            }
        }
    }

    public Task ModifyOrderAsync(Guid orderId, Price newStopLoss, Price? newTakeProfit, CancellationToken ct)
    {
        if (_openTrades.TryGetValue(orderId, out var trade))
        {
            _openTrades[orderId] = trade with { StopLoss = newStopLoss, TakeProfit = newTakeProfit };
#if DEBUG
            _logger.LogDebug("BacktestReplay: SL modified {Id} → {Sl:F5} tp={Tp}", orderId, newStopLoss.Value, newTakeProfit?.Value);
#endif
        }
        return Task.CompletedTask;
    }

    public Task CancelOrderAsync(Guid orderId, CancellationToken ct)
        => Task.CompletedTask;

    // Called on the ENGINE thread (RunBacktestLoopAsync) before each bar is processed. This is where
    // we advance the venue clock/price, match any resting limit orders against the bar's range, AND
    // publish the per-bar mark-to-market equity so the breach watchdog and the equity curve see the
    // real open book (F1 iter-26).
    //
    // iter-redesign-ctrader P1.4: also detect SL/TP hits against the bar's OHLC here — the venue owns
    // exit execution, so it emits reasoned close events (CloseReason="SL"/"TP") exactly like cTrader.
    public void OnBarObserved(Bar bar)
    {
        _lastClose = bar.Close;
        BrokerTimeUtc = bar.OpenTimeUtc + BarDuration(bar.Timeframe);
        ProcessPendingLimits(bar);
        ProcessPendingStops(bar);
        ProcessSlTpHits(bar);
        EmitAccountUpdate(BrokerTimeUtc);
    }

    // P2.7: match resting STOP entry orders against the bar that just became current — the mirror image
    // of ProcessPendingLimits. A buy stop fills when price rises UP THROUGH the trigger (the ask side,
    // same long-entry spread convention as a market/limit buy); a sell stop fills when price falls DOWN
    // THROUGH it (raw bid, same short-entry convention as a market/limit sell). Gap-through: if the
    // bar's OPEN already lies beyond the trigger (price gapped past it before this bar started), fill at
    // the bar's OPEN instead of the trigger price — the same rule ProcessSlTpHits already applies to SL
    // gap-through, reused here for stop-entry gap-through. Same expiry semantics as limits.
    private void ProcessPendingStops(Bar bar)
    {
        if (_pendingStops.Count == 0) return;

        var spread = GetSpread();

        foreach (var (orderId, stop) in _pendingStops.ToList())
        {
            var reached = stop.Direction == TradeDirection.Long
                ? SpreadConvention.AskPrice(bar.High, spread) >= stop.StopPrice
                : bar.Low <= stop.StopPrice;

            if (reached)
            {
                _pendingStops.Remove(orderId);
                var fillPrice = stop.StopPrice;
                if (stop.Direction == TradeDirection.Long)
                {
                    var askOpen = SpreadConvention.AskPrice(bar.Open, spread);
                    if (askOpen >= stop.StopPrice) fillPrice = askOpen;
                }
                else if (bar.Open <= stop.StopPrice)
                {
                    fillPrice = bar.Open;
                }

                FillEntry(orderId, stop.Direction, fillPrice, stop.Lots, stop.StopLoss, stop.TakeProfit);
                _logger.LogDebug("BacktestReplay: stop fill {Id} at {Price:F5} dir={Dir}",
                    orderId, fillPrice, stop.Direction);
                continue;
            }

            stop.BarsRemaining--;
            if (stop.BarsRemaining <= 0)
            {
                _pendingStops.Remove(orderId);
                EmitExecutionEvent(new ExecutionEvent(
                    orderId, OrderState.Cancelled, null, 0, "ENTRY_EXPIRED", BrokerTimeUtc) { Symbol = _symbol });
                _logger.LogDebug("BacktestReplay: stop expired {Id} at {Price:F5}", orderId, stop.StopPrice);
            }
        }
    }

    // iter-redesign-ctrader P1.4: detect SL/TP hits against the bar's OHLC and emit reasoned close
    // events — same contract as cTrader. Uses the SAME stateless detection as the engine's
    // (EngineReducer.DetectSlTpExit) so the replay exit behaviour is byte-identical.
    private void ProcessSlTpHits(Bar bar)
    {
        if (_openTrades.Count == 0) return;

        var spread = GetSpread();

        foreach (var (orderId, trade) in _openTrades.ToList())
        {
            // A short's SL/TP is crossed by the ASK (it closes by buying back); shift the whole bar to
            // the ask side for detection. A long's SL/TP is crossed by the raw bid bar (unchanged).
            var checkBar = trade.Direction == TradeDirection.Short
                ? SpreadConvention.AskBar(bar, spread)
                : bar;

            var reason = EngineReducer.DetectSlTpExit(
                trade.Direction, trade.StopLoss, trade.TakeProfit, checkBar);

            if (reason is null) continue;

            var fillPrice = reason == "TP" && trade.TakeProfit is { } tp
                ? tp.Value
                : trade.StopLoss.Value;

            if (trade.Direction == TradeDirection.Short)
                fillPrice = SpreadConvention.AskPrice(fillPrice, spread);

            if (reason == "SL")
            {
                if (trade.Direction == TradeDirection.Long && checkBar.Open <= trade.StopLoss.Value)
                    fillPrice = checkBar.Open;
                else if (trade.Direction == TradeDirection.Short && checkBar.Open >= trade.StopLoss.Value)
                    fillPrice = checkBar.Open;
            }

            var costs = ComputeCosts(trade, fillPrice);
            _balance += costs.NetProfit;
            _openTrades.Remove(orderId);

            EmitExecutionEvent(new ExecutionEvent(
                orderId, OrderState.Filled, new Price(fillPrice), trade.Lots, null, BrokerTimeUtc)
            {
                GrossProfit = costs.GrossProfit,
                Commission = costs.Commission,
                Swap = costs.Swap,
                NetProfit = costs.NetProfit,
                Symbol = _symbol,
                CloseReason = reason,
            });

            EmitAccountUpdate(BrokerTimeUtc);
            _logger.LogDebug("BacktestReplay: SL/TP exit {Id} reason={Reason} at {Price:F5} net={Net:F2}",
                orderId, reason, fillPrice, costs.NetProfit);
        }
    }

    public void SyncToBar(decimal close, DateTime openTimeUtc)
    {
        _lastClose = close;
        BrokerTimeUtc = openTimeUtc;
        EmitAccountUpdate(openTimeUtc);
    }

    private static TimeSpan BarDuration(Timeframe tf) => tf switch
    {
        Timeframe.M1 => TimeSpan.FromMinutes(1),
        Timeframe.M5 => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.M30 => TimeSpan.FromMinutes(30),
        Timeframe.H1 => TimeSpan.FromHours(1),
        Timeframe.H4 => TimeSpan.FromHours(4),
        Timeframe.D1 => TimeSpan.FromDays(1),
        Timeframe.W1 => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(1),
    };

    // Publishes balance + floating PnL of the current open book. Best-effort write (bounded
    // DropOldest channel) so it never blocks the engine loop.
    private void EmitAccountUpdate(DateTime ts)
    {
        var floatingPnL = ComputeFloatingPnL(_lastClose);
        var equity = _balance + floatingPnL;
        _accountChannel.Writer.TryWrite(new AccountUpdate(_balance, equity, floatingPnL, ts));
    }

    public Task ClosePositionAsync(Guid positionId, CancellationToken ct)
    {
        var spread = GetSpread();
        var mid = _lastClose > 0 ? _lastClose : 1m;
        if (_openTrades.TryGetValue(positionId, out var trade))
        {
            // Long closes by selling at bid (raw); short closes by buying at ask (bid + full spread).
            var exitPrice = trade.Direction == TradeDirection.Long ? mid : SpreadConvention.AskPrice(mid, spread);
            return CloseAtAsync(positionId, new Price(exitPrice), ct);
        }
        return CloseAtAsync(positionId, new Price(mid), ct);
    }

    // F2/D3 (iter-26): an engine-detected SL/TP exit closes at the stop/target price, not the bar
    // close, so backtest PnL reflects what the stop actually cost.
    public Task ClosePositionAtAsync(Guid positionId, Price exitPrice, CancellationToken ct)
        => CloseAtAsync(positionId, exitPrice, ct);

    private Task CloseAtAsync(Guid positionId, Price fillPrice, CancellationToken ct)
    {
        if (_openTrades.TryGetValue(positionId, out var trade))
        {
            var costs = ComputeCosts(trade, fillPrice.Value);
            _balance += costs.NetProfit;
            _openTrades.Remove(positionId);

            // Stamp the itemised economics on the close fill so the engine ledger (and the DB/report)
            // match the account — net of commission/swap, not a cost-free recompute downstream.
            // H14 (iter-35 B2): report the closed lot size (was 0). FilledLots == position lots keeps
            // this a FULL close in the lifecycle FSM (the partial branch needs FilledLots < lots), while
            // the order ledger / reconciliation now see the real volume instead of zero.
            EmitExecutionEvent(new ExecutionEvent(
                positionId, OrderState.Filled, fillPrice, trade.Lots, null, BrokerTimeUtc)
            {
                GrossProfit = costs.GrossProfit,
                Commission = costs.Commission,
                Swap = costs.Swap,
                NetProfit = costs.NetProfit,
                Symbol = _symbol,
            });

            // F1: surface the realized balance change as an account update so equity/drawdown move.
            EmitAccountUpdate(BrokerTimeUtc);
            _logger.LogDebug("BacktestReplay: close {PositionId} at {Price:F5} gross={Gross:F2} comm={Comm:F2} swap={Swap:F2} net={Net:F2} balance={Balance:F2}",
                positionId, fillPrice.Value, costs.GrossProfit, costs.Commission, costs.Swap, costs.NetProfit, _balance);
        }
        else
        {
            // No tracked trade (unknown / already closed) — surface a cost-free fill so the lifecycle
            // FSM can still resolve.
            EmitExecutionEvent(
                new ExecutionEvent(positionId, OrderState.Filled, fillPrice, 0, null, BrokerTimeUtc) { Symbol = _symbol });
        }

        return Task.CompletedTask;
    }

    private TradeCosts ComputeCosts(OpenTrade trade, decimal exitPrice)
    {
        try
        {
            var symbolInfo = _symbolRegistry.Get(_symbol);
            return TradeCostCalculator.Compute(
                trade.Direction, new Price(trade.EntryPrice), new Price(exitPrice), trade.Lots,
                symbolInfo, _crossRateProvider, trade.OpenedAtUtc, BrokerTimeUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute costs for {Symbol} at exit {ExitPrice} — using gross PnL only", _symbol, exitPrice);
            // M10 (iter-35 B2): don't swallow gross PnL to zero. Commission/swap may be zero,
            // but the trade's directional profit is still computable without symbol info.
            var grossPnl = trade.Direction == TradeDirection.Long
                ? (exitPrice - trade.EntryPrice) * trade.Lots
                : (trade.EntryPrice - exitPrice) * trade.Lots;
            return new TradeCosts(grossPnl, 0, 0, grossPnl, 0);
        }
    }

    public Task ClosePartialPositionAsync(Guid positionId, decimal lots, CancellationToken ct)
    {
        var fillPrice = new Price(_lastClose > 0 ? _lastClose : 1m);

        if (_openTrades.TryGetValue(positionId, out var trade))
        {
            var partialTrade = trade with { Lots = lots };
            var costs = ComputeCosts(partialTrade, fillPrice.Value);
            _balance += costs.NetProfit;

            EmitExecutionEvent(new ExecutionEvent(
                positionId, OrderState.Filled, fillPrice, lots, null, BrokerTimeUtc)
            {
                GrossProfit = costs.GrossProfit,
                Commission = costs.Commission,
                Swap = costs.Swap,
                NetProfit = costs.NetProfit,
                Symbol = _symbol,
            });

            var remaining = trade.Lots - lots;
            if (remaining <= 0m)
                _openTrades.Remove(positionId);
            else
                _openTrades[positionId] = trade with { Lots = remaining };
            // F1: realized partial PnL must reach the account stream too.
            EmitAccountUpdate(BrokerTimeUtc);
            _logger.LogDebug("BacktestReplay: partial close {PositionId} lots={Lots} remaining={Remaining} gross={Gross:F2} net={Net:F2}",
                positionId, lots, remaining, costs.GrossProfit, costs.NetProfit);
        }
        else
        {
            EmitExecutionEvent(
                new ExecutionEvent(positionId, OrderState.Filled, fillPrice, lots, null, BrokerTimeUtc) { Symbol = _symbol });
        }

        return Task.CompletedTask;
    }

    private decimal ComputeFloatingPnL(decimal close)
    {
        if (_openTrades.Count == 0) return 0m;

        try
        {
            var symbolInfo = _symbolRegistry.Get(_symbol);
            var spread = symbolInfo.TypicalSpread;
            var pipValue = PipCalculator.PipValuePerLot(symbolInfo, close, _crossRateProvider);
            var total = 0m;
            foreach (var (_, t) in _openTrades)
            {
                // P0.2 (D3): a long's exit is a sell at bid (raw close, no adjustment); a short's exit is
                // a buy at ask (close + full spread).
                var effectiveClose = t.Direction == TradeDirection.Long ? close : SpreadConvention.AskPrice(close, spread);
                var diff = t.Direction == TradeDirection.Long ? effectiveClose - t.EntryPrice : t.EntryPrice - effectiveClose;
                total += (diff / symbolInfo.PipSize) * pipValue * t.Lots;
            }
            return total;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute floating PnL");
            return 0m;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _feedCts?.Cancel();
        _barChannel.Writer.TryComplete();
        _tickChannel.Writer.TryComplete();
        _accountChannel.Writer.TryComplete();
        _executionChannel.Writer.TryComplete();
        _openTrades.Clear();
        _pendingLimits.Clear();
        _pendingStops.Clear();
        try { await _feedTask; } catch (OperationCanceledException) { }
        _feedCts?.Dispose();
    }
}

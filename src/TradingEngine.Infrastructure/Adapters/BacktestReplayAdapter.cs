using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingEngine.Services.Helpers;

namespace TradingEngine.Infrastructure.Adapters;

public sealed class BacktestReplayAdapter : IBrokerAdapter, IAsyncDisposable
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
    private decimal _balance;
    private decimal _lastClose;
    private Task _feedTask = Task.CompletedTask;
    private CancellationTokenSource? _feedCts;

    private sealed record OpenTrade(TradeDirection Direction, decimal EntryPrice, decimal Lots, DateTime OpenedAtUtc);
    private sealed class PendingLimit
    {
        public required TradeDirection Direction { get; init; }
        public required decimal Lots { get; init; }
        public required decimal LimitPrice { get; init; }
        public int BarsRemaining { get; set; }
    }

    public bool IsConnected { get; private set; }
    public ChannelReader<Tick> TickStream => _tickChannel.Reader;
    public ChannelReader<Bar> BarStream => _barChannel.Reader;
    public ChannelReader<AccountUpdate> AccountStream => _accountChannel.Reader;
    public ChannelReader<ExecutionEvent> ExecutionStream => _executionChannel.Reader;
    public int BarCount { get; private set; }
    public DateTime BrokerTimeUtc { get; private set; }

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
                    new Tick(bar.Symbol, bar.Close, bar.Close + 0.0001m, bar.OpenTimeUtc), ct);
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
        => Task.FromResult(new AccountState(_initialBalance, _initialBalance, []));

    public Task<Guid> SubmitOrderAsync(OrderRequest request, CancellationToken ct)
    {
        var orderId = Guid.NewGuid();

        // Resting limit order: hold it until a later bar's range reaches the limit price, or until it
        // expires. Market orders (and limits with no price) fill instantly at the last close.
        if (request.Type == OrderType.Limit && request.LimitPrice is { } limit)
        {
            _pendingLimits[orderId] = new PendingLimit
            {
                Direction = request.Direction,
                Lots = request.Lots,
                LimitPrice = limit.Value,
                BarsRemaining = request.Intent.Entry?.LimitOrderExpiryBars ?? 3,
            };
            _logger.LogDebug("BacktestReplay: limit rest {Id} at {Price:F5} dir={Dir} lots={Lots} expiry={Bars}b",
                orderId, limit.Value, request.Direction, request.Lots, _pendingLimits[orderId].BarsRemaining);
            return Task.FromResult(orderId);
        }

        var fillPrice = new Price(_lastClose > 0 ? _lastClose : 1m);
        FillEntry(orderId, request.Direction, fillPrice.Value, request.Lots);
        _logger.LogDebug("BacktestReplay: instant fill {Id} at {Price:F5} dir={Dir} lots={Lots}",
            orderId, fillPrice.Value, request.Direction, request.Lots);
        return Task.FromResult(orderId);
    }

    private void FillEntry(Guid orderId, TradeDirection direction, decimal fillPrice, decimal lots)
    {
        _executionChannel.Writer.TryWrite(
            new ExecutionEvent(orderId, OrderState.Filled, new Price(fillPrice), lots, null, BrokerTimeUtc));
        _openTrades[orderId] = new OpenTrade(direction, fillPrice, lots, BrokerTimeUtc);
    }

    // Match resting limit orders against the bar that just became current. A buy limit fills when the
    // bar trades down to (or below) the limit; a sell limit when it trades up to it. Fill is at the
    // limit price (no slippage). Orders that don't fill burn one bar of life and, once expired, emit a
    // cancellation carrying ENTRY_EXPIRED so the engine journals the expiry instead of a phantom fill.
    private void ProcessPendingLimits(Bar bar)
    {
        if (_pendingLimits.Count == 0) return;

        foreach (var (orderId, limit) in _pendingLimits.ToList())
        {
            var reached = limit.Direction == TradeDirection.Long
                ? bar.Low <= limit.LimitPrice
                : bar.High >= limit.LimitPrice;

            if (reached)
            {
                _pendingLimits.Remove(orderId);
                FillEntry(orderId, limit.Direction, limit.LimitPrice, limit.Lots);
                _logger.LogDebug("BacktestReplay: limit fill {Id} at {Price:F5} dir={Dir}",
                    orderId, limit.LimitPrice, limit.Direction);
                continue;
            }

            limit.BarsRemaining--;
            if (limit.BarsRemaining <= 0)
            {
                _pendingLimits.Remove(orderId);
                _executionChannel.Writer.TryWrite(new ExecutionEvent(
                    orderId, OrderState.Cancelled, null, 0, "ENTRY_EXPIRED", BrokerTimeUtc));
                _logger.LogDebug("BacktestReplay: limit expired {Id} at {Price:F5}", orderId, limit.LimitPrice);
            }
        }
    }

    public Task ModifyOrderAsync(Guid orderId, Price newStopLoss, Price? newTakeProfit, CancellationToken ct)
        => Task.CompletedTask;

    public Task CancelOrderAsync(Guid orderId, CancellationToken ct)
        => Task.CompletedTask;

    // Called on the ENGINE thread (RunBacktestLoopAsync) before each bar is processed. This is where
    // we advance the venue clock/price, match any resting limit orders against the bar's range, AND
    // publish the per-bar mark-to-market equity so the breach watchdog and the equity curve see the
    // real open book (F1 iter-26).
    public void OnBarObserved(Bar bar)
    {
        _lastClose = bar.Close;
        BrokerTimeUtc = bar.OpenTimeUtc + BarDuration(bar.Timeframe);
        ProcessPendingLimits(bar);
        EmitAccountUpdate(BrokerTimeUtc);
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
        => CloseAtAsync(positionId, new Price(_lastClose > 0 ? _lastClose : 1m), ct);

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
            _executionChannel.Writer.TryWrite(new ExecutionEvent(
                positionId, OrderState.Filled, fillPrice, trade.Lots, null, BrokerTimeUtc)
            {
                GrossProfit = costs.GrossProfit,
                Commission = costs.Commission,
                Swap = costs.Swap,
                NetProfit = costs.NetProfit,
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
            _executionChannel.Writer.TryWrite(
                new ExecutionEvent(positionId, OrderState.Filled, fillPrice, 0, null, BrokerTimeUtc));
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

            _executionChannel.Writer.TryWrite(new ExecutionEvent(
                positionId, OrderState.Filled, fillPrice, lots, null, BrokerTimeUtc)
            {
                GrossProfit = costs.GrossProfit,
                Commission = costs.Commission,
                Swap = costs.Swap,
                NetProfit = costs.NetProfit,
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
            _executionChannel.Writer.TryWrite(
                new ExecutionEvent(positionId, OrderState.Filled, fillPrice, lots, null, BrokerTimeUtc));
        }

        return Task.CompletedTask;
    }

    private decimal ComputeFloatingPnL(decimal close)
    {
        if (_openTrades.Count == 0) return 0m;

        try
        {
            var symbolInfo = _symbolRegistry.Get(_symbol);
            var halfSpread = symbolInfo.TypicalSpread / 2m;
            var pipValue = PipCalculator.PipValuePerLot(symbolInfo, close, _crossRateProvider);
            var total = 0m;
            foreach (var (_, t) in _openTrades)
            {
                // H16: use directional bid/ask instead of mid. For longs the exit is at bid (lower);
                // for shorts at ask (higher). This prices floating PnL conservatively.
                var effectiveClose = t.Direction == TradeDirection.Long ? close - halfSpread : close + halfSpread;
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
        try { await _feedTask; } catch (OperationCanceledException) { }
        _feedCts?.Dispose();
    }
}

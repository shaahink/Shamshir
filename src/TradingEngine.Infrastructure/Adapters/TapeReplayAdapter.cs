using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingEngine.Domain;
using TradingEngine.Engine;
using TradingEngine.Services.Helpers;

namespace TradingEngine.Infrastructure.Adapters;

/// <summary>
/// iter-marketdata-tape P3 — the fast, in-process FAKE VENUE. Sources bars from the canonical
/// <see cref="IMarketDataStore"/> (downloaded history, deduped) instead of cTrader-cli/NetMQ, so a backtest
/// crosses no process boundary. Models the same economics as <see cref="BacktestReplayAdapter"/>
/// (commission/swap/spread + venue-managed SL/TP via <see cref="EngineReducer.DetectSlTpExit"/>), but adds
/// DUAL-RESOLUTION exits: the strategy still decides on the decision timeframe, while SL/TP/limit fills are
/// evaluated against a FINER timeframe (default m1) within each decision bar — recovering intrabar
/// long-shadow / SL-before-TP fidelity that a single decision-bar OHLC can't express. Falls back to
/// decision-bar-resolution exits when no finer data is stored. Kernel/decision logic is untouched.
///
/// F6 (documented): Gap-through slippage — when a fine bar OPENS beyond the stop price, the fill
/// price is the bar's open (not the stop), modelling real market gap-through. This is handled in
/// the fine-bar SL/TP detection loop.
///
/// F7 (documented): Fine bars in decision-TF gaps — when the fine-bar data has gaps within a
/// decision bar (e.g., weekends, missing candles), the per-bar high/low watermarks still provide a
/// reasonable envelope. True tick-level gap-through fidelity requires per-bar recorded spread (A3).
/// </summary>
public sealed class TapeReplayAdapter : IBrokerAdapter, IReplayVenue, IAsyncDisposable
{
    private readonly IMarketDataStore _store;
    private readonly Symbol _symbol;
    private readonly Timeframe _decisionTf;
    private readonly Timeframe _exitTf;
    private readonly DateTime _from;
    private readonly DateTime _to;
    private readonly decimal _initialBalance;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly Func<string, string, decimal> _crossRateProvider;
    private readonly ILogger<TapeReplayAdapter> _logger;

    private readonly TimeSpan _decisionInterval;
    private readonly TimeSpan _exitInterval;

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
    private volatile float _speed = 10f;
    private readonly ManualResetEventSlim _pauseEvent = new(true);

    public float Speed
    {
        get => _speed;
        set
        {
            _speed = Math.Clamp(value, 0f, 10f);
            if (_speed > 0f) _pauseEvent.Set();
            else _pauseEvent.Reset();
        }
    }

    // Finer-resolution bars for exit detection, plus a monotonic cursor consumed by OnBarObserved.
    private IReadOnlyList<Bar> _exitBars = [];
    private int _exitIndex;
    private DateTime _lastDecisionBarTime = DateTime.MinValue;

    private sealed record OpenTrade(
        TradeDirection Direction, decimal EntryPrice, decimal Lots, DateTime OpenedAtUtc, Price StopLoss, Price? TakeProfit);

    private sealed class PendingLimit
    {
        public required TradeDirection Direction { get; init; }
        public required decimal Lots { get; init; }
        public required decimal LimitPrice { get; init; }
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

    public string? ExitResolution { get; private set; }

    // Venue owns exits (same model as cTrader / BacktestReplayAdapter): the engine never runs bar-by-bar
    // exit detection for this venue; the venue reports reasoned closes.
    public ExitMode ExitMode => ExitMode.VenueManaged;
    public IReadOnlySet<Guid> GetOpenPositionIds() => _openTrades.Keys.ToHashSet();

    public TapeReplayAdapter(
        IMarketDataStore store,
        Symbol symbol,
        Timeframe decisionTf,
        Timeframe exitTf,
        DateTime from,
        DateTime to,
        decimal initialBalance,
        ISymbolInfoRegistry symbolRegistry,
        Func<string, string, decimal> crossRateProvider,
        ILogger<TapeReplayAdapter> logger)
    {
        _store = store;
        _symbol = symbol;
        _decisionTf = decisionTf;
        _exitTf = exitTf;
        _from = from;
        _to = to;
        _initialBalance = initialBalance;
        _balance = initialBalance;
        _symbolRegistry = symbolRegistry;
        _crossRateProvider = crossRateProvider;
        _logger = logger;
        _decisionInterval = decisionTf.ToTimeSpan();
        _exitInterval = exitTf.ToTimeSpan();
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        IsConnected = true;
        _feedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Load finer bars for dual-resolution exits ONCE, up front (consumed synchronously on the engine
        // thread by OnBarObserved). If the exit timeframe equals the decision timeframe, or no finer data
        // exists, we run single-resolution exits on the decision bar itself.
        if (_exitTf != _decisionTf)
        {
            _exitBars = await _store.ReadBarsAsync(_symbol, _exitTf, _from, _to, ct);
            if (_exitBars.Count == 0)
            {
                _logger.LogWarning("TapeReplay: no {ExitTf} bars stored for {Symbol} — falling back to {DecisionTf}-resolution exits",
                    _exitTf, _symbol, _decisionTf);
                ExitResolution = $"{_decisionTf} (fallback — no {_exitTf} bars)";
            }
            else
            {
                ExitResolution = _exitTf == Timeframe.M1 ? "M1" : _exitTf.ToString();
            }
        }
        else
        {
            ExitResolution = _decisionTf.ToString();
        }

        await _accountChannel.Writer.WriteAsync(
            new AccountUpdate(_initialBalance, _initialBalance, 0, _from), ct);

        _feedTask = FeedBarsAsync(_feedCts.Token);
    }

    private async Task FeedBarsAsync(CancellationToken ct)
    {
        try
        {
            var bars = await _store.ReadBarsAsync(_symbol, _decisionTf, _from, _to, ct);
            BarCount = bars.Count;
            _logger.LogInformation("TapeReplay: loaded {Count} {Tf} decision bars + {ExitCount} {ExitTf} exit bars for {Symbol}",
                bars.Count, _decisionTf, _exitBars.Count, _exitTf, _symbol);

            if (bars.Count == 0)
            {
                _logger.LogWarning("TapeReplay: no {Tf} bars for {Symbol} in [{From}–{To}]. Download history first.",
                    _decisionTf, _symbol, _from, _to);
            }

            foreach (var bar in bars)
            {
                ct.ThrowIfCancellationRequested();
                _pauseEvent.Wait(ct);
                var delayMs = _speed > 0f ? (int)(100f / _speed) : Timeout.Infinite;
                if (delayMs > 0) await Task.Delay(delayMs, ct);
                await _barChannel.Writer.WriteAsync(bar, ct);
                await _tickChannel.Writer.WriteAsync(
                    new Tick(bar.Symbol, bar.Close, SpreadConvention.AskPrice(bar.Close, GetSpread()), bar.OpenTimeUtc), ct);
            }
        }
        catch (OperationCanceledException) { _logger.LogDebug("TapeReplay: feed cancelled"); }
        finally
        {
            _barChannel.Writer.TryComplete();
            _tickChannel.Writer.TryComplete();
        }
    }

    // Called on the ENGINE thread before each decision bar is evaluated. Advances the venue and detects
    // exits — at finer resolution when finer bars are available (dual-resolution), else on the decision bar.
    public void OnBarObserved(Bar decisionBar)
    {
        var isNewDecisionBar = decisionBar.OpenTimeUtc != _lastDecisionBarTime;
        if (isNewDecisionBar)
            _lastDecisionBarTime = decisionBar.OpenTimeUtc;

        if (_exitBars.Count == 0)
        {
            _lastClose = decisionBar.Close;
            BrokerTimeUtc = decisionBar.OpenTimeUtc + _decisionInterval;
            ProcessPendingLimits(decisionBar, decrementExpiry: isNewDecisionBar);
            ProcessSlTpHits(decisionBar);
            EmitAccountUpdate(BrokerTimeUtc);
            return;
        }

        var windowEnd = decisionBar.OpenTimeUtc + _decisionInterval;
        var minEquity = _balance;
        while (_exitIndex < _exitBars.Count && _exitBars[_exitIndex].OpenTimeUtc < windowEnd)
        {
            var fine = _exitBars[_exitIndex];
            _exitIndex++;
            if (fine.OpenTimeUtc < decisionBar.OpenTimeUtc) continue;
            _lastClose = fine.Close;
            BrokerTimeUtc = fine.OpenTimeUtc + _exitInterval;
            ProcessPendingLimits(fine, decrementExpiry: true);
            ProcessSlTpHits(fine);
            var floatingEquity = _balance + ComputeFloatingPnL(fine.Close);
            if (floatingEquity < minEquity) minEquity = floatingEquity;
        }

        _lastClose = decisionBar.Close;
        BrokerTimeUtc = windowEnd;
        EmitAccountUpdate(BrokerTimeUtc, minEquity);
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
        var orderId = request.ClientOrderId ?? Guid.NewGuid();
        var sl = request.Intent.StopLoss;
        var tp = request.Intent.TakeProfit;

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
            return Task.FromResult(orderId);
        }

        var midPrice = _lastClose > 0 ? _lastClose : 1m;
        var spread = GetSpread();
        var fillPrice = request.Direction == TradeDirection.Long ? SpreadConvention.AskPrice(midPrice, spread) : midPrice;
        FillEntry(orderId, request.Direction, fillPrice, request.Lots, sl, tp);
        return Task.FromResult(orderId);
    }

    private void EmitExecutionEvent(ExecutionEvent evt)
    {
        if (!_executionChannel.Writer.TryWrite(evt))
            _logger.LogError("TapeReplay: execution channel full — event dropped; orderId={OrderId}", evt.OrderId);
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

    // A buy limit fills when a bar trades down to the limit; a sell limit when it trades up to it. Fill at
    // the limit price. Unfilled orders burn one decision bar of life and expire with ENTRY_EXPIRED. Note:
    // BarsRemaining is decremented per FINER bar in dual-resolution mode, so expiry counts sub-bars — the
    // engine's own re-proposal cadence still governs strategy behaviour; this only bounds a resting fill.
    private void ProcessPendingLimits(Bar bar, bool decrementExpiry = true)
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
                continue;
            }

            if (!decrementExpiry) continue;

            limit.BarsRemaining--;
            if (limit.BarsRemaining <= 0)
            {
                _pendingLimits.Remove(orderId);
                EmitExecutionEvent(new ExecutionEvent(
                    orderId, OrderState.Cancelled, null, 0, "ENTRY_EXPIRED", BrokerTimeUtc) { Symbol = _symbol });
            }
        }
    }

    // Detect SL/TP hits against a bar's OHLC using the SAME stateless detection as the kernel/replay, so exit
    // behaviour is consistent across venues. In dual-resolution mode this runs per finer bar, so the FIRST
    // finer bar to touch a level wins — recovering intrabar SL-before-TP ordering and long-shadow fills.
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

            var reason = EngineReducer.DetectSlTpExit(trade.Direction, trade.StopLoss, trade.TakeProfit, checkBar);
            if (reason is null) continue;

            var fillPrice = reason == "TP" && trade.TakeProfit is { } tp ? tp.Value : trade.StopLoss.Value;
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
        }
    }

    public Task ModifyOrderAsync(Guid orderId, Price newStopLoss, Price? newTakeProfit, CancellationToken ct)
    {
        if (_openTrades.TryGetValue(orderId, out var trade))
            _openTrades[orderId] = trade with { StopLoss = newStopLoss, TakeProfit = newTakeProfit };
        return Task.CompletedTask;
    }

    public Task CancelOrderAsync(Guid orderId, CancellationToken ct) => Task.CompletedTask;

    public Task ClosePositionAsync(Guid positionId, CancellationToken ct)
    {
        var spread = GetSpread();
        var mid = _lastClose > 0 ? _lastClose : 1m;
        // Long closes by selling at bid (raw); short closes by buying at ask (bid + full spread).
        if (_openTrades.TryGetValue(positionId, out var trade))
        {
            var exitPrice = trade.Direction == TradeDirection.Long ? mid : SpreadConvention.AskPrice(mid, spread);
            return CloseAtAsync(positionId, new Price(exitPrice));
        }
        return CloseAtAsync(positionId, new Price(mid));
    }

    public Task ClosePositionAtAsync(Guid positionId, Price exitPrice, CancellationToken ct)
        => CloseAtAsync(positionId, exitPrice);

    private Task CloseAtAsync(Guid positionId, Price fillPrice)
    {
        if (_openTrades.TryGetValue(positionId, out var trade))
        {
            var costs = ComputeCosts(trade, fillPrice.Value);
            _balance += costs.NetProfit;
            _openTrades.Remove(positionId);

            EmitExecutionEvent(new ExecutionEvent(
                positionId, OrderState.Filled, fillPrice, trade.Lots, null, BrokerTimeUtc)
            {
                GrossProfit = costs.GrossProfit,
                Commission = costs.Commission,
                Swap = costs.Swap,
                NetProfit = costs.NetProfit,
                Symbol = _symbol,
            });
            EmitAccountUpdate(BrokerTimeUtc);
        }
        else
        {
            EmitExecutionEvent(
                new ExecutionEvent(positionId, OrderState.Filled, fillPrice, 0, null, BrokerTimeUtc) { Symbol = _symbol });
        }
        return Task.CompletedTask;
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
            if (remaining <= 0m) _openTrades.Remove(positionId);
            else _openTrades[positionId] = trade with { Lots = remaining };
            EmitAccountUpdate(BrokerTimeUtc);
        }
        else
        {
            EmitExecutionEvent(
                new ExecutionEvent(positionId, OrderState.Filled, fillPrice, lots, null, BrokerTimeUtc) { Symbol = _symbol });
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
            _logger.LogWarning(ex, "TapeReplay: cost calc failed for {Symbol} at exit {ExitPrice} — gross only", _symbol, exitPrice);
            var grossPnl = trade.Direction == TradeDirection.Long
                ? (exitPrice - trade.EntryPrice) * trade.Lots
                : (trade.EntryPrice - exitPrice) * trade.Lots;
            return new TradeCosts(grossPnl, 0, 0, grossPnl, 0);
        }
    }

    private void EmitAccountUpdate(DateTime ts, decimal? minEquity = null)
    {
        var floatingPnL = ComputeFloatingPnL(_lastClose);
        var equity = minEquity ?? (_balance + floatingPnL);
        _accountChannel.Writer.TryWrite(new AccountUpdate(_balance, equity, floatingPnL, ts));
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
                // P0.2 (D3): a long's exit is a sell at bid (raw close); a short's exit is a buy at ask
                // (close + full spread).
                var effectiveClose = t.Direction == TradeDirection.Long ? close : SpreadConvention.AskPrice(close, spread);
                var diff = t.Direction == TradeDirection.Long ? effectiveClose - t.EntryPrice : t.EntryPrice - effectiveClose;
                total += (diff / symbolInfo.PipSize) * pipValue * t.Lots;
            }
            return total;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TapeReplay: floating PnL calc failed");
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

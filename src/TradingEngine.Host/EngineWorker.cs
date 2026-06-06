using System.Collections.Concurrent;

namespace TradingEngine.Host;

public sealed class EngineWorker : BackgroundService
{
    private readonly IBrokerAdapter _broker;
    private readonly IRiskManager _riskManager;
    private readonly IPositionManager _positionManager;
    private readonly IEnumerable<IStrategy> _strategies;
    private readonly IIndicatorService _indicators;
    private readonly IEventBus _eventBus;
    private readonly IEngineClock _clock;
    private readonly DataFeedService? _dataFeed;
    private readonly ILogger<EngineWorker> _logger;

    private readonly ConcurrentDictionary<Symbol, ConcurrentDictionary<Timeframe, List<Bar>>> _bars = new();
    private readonly ConcurrentDictionary<string, double> _indicatorValues = new();

    private readonly Channel<ExecutionEvent> _executionEventChannel =
        Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true
        });

    private AccountUpdate? _latestAccountUpdate;
    private long _tickCount;
    private long _barCount;

    public EngineWorker(
        IBrokerAdapter broker,
        IRiskManager riskManager,
        IPositionManager positionManager,
        IEnumerable<IStrategy> strategies,
        IIndicatorService indicators,
        IEventBus eventBus,
        IEngineClock clock,
        ILogger<EngineWorker> logger,
        DataFeedService? dataFeed = null)
    {
        _broker = broker;
        _riskManager = riskManager;
        _positionManager = positionManager;
        _strategies = strategies;
        _indicators = indicators;
        _eventBus = eventBus;
        _clock = clock;
        _dataFeed = dataFeed;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Engine starting. Mode={Mode} Strategies={Count}",
            EngineMode.Backtest, _strategies.Count());

        await _broker.ConnectAsync(ct);
        await WarmUpIndicatorsAsync(ct);

        var tasks = new[]
        {
            ProcessTicksAsync(ct),
            ProcessBarsAsync(ct),
            ProcessAccountUpdatesAsync(ct),
            ProcessExecutionEventsAsync(ct),
        };

        await Task.WhenAll(tasks);
        _logger.LogInformation("Engine stopped. Ticks={Ticks} Bars={Bars}", Interlocked.Read(ref _tickCount), Interlocked.Read(ref _barCount));
    }

    private async Task ProcessTicksAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Tick processor started");
            await foreach (var tick in _broker.TickStream.ReadAllAsync(ct))
            {
                Interlocked.Increment(ref _tickCount);

                while (_executionEventChannel.Reader.TryRead(out var execEvent))
                    HandleExecutionEvent(execEvent);

                var accountUpdate = Interlocked.Exchange(ref _latestAccountUpdate, null);
                if (accountUpdate is not null)
                    HandleAccountUpdate(accountUpdate);

                if (_dataFeed is not null)
                {
                    var broker = _broker as SimulatedBrokerAdapter;
                    broker?.OnTickReceived(tick);
                }

                foreach (var strategy in _strategies)
                {
                    var barSnapshot = BuildBarSnapshot(tick.Symbol);
                    if (barSnapshot is null) continue;

                    if (barSnapshot.Values.Sum(b => b.Count) < strategy.RequiredBarCount)
                        continue;

                    var indicators = _indicatorValues.ToDictionary(kv => kv.Key, kv => kv.Value);
                    var context = new MarketContext(
                        tick.Symbol, tick, barSnapshot, indicators, _clock.UtcNow);

                    var intent = strategy.Evaluate(context);
                    if (intent is null) continue;

                    var equity = new EquitySnapshot(
                        _clock.UtcNow, 0, 0, 0, 0, 0, 0, 0, EngineMode.Backtest);
                    var violations = _riskManager.Validate(intent, equity);

                    if (violations.Count > 0)
                    {
                        _logger.LogWarning("Trade blocked. Strategy={Strategy} Symbol={Symbol} Violations={V}",
                            strategy.Id, intent.Symbol,
                            string.Join(", ", violations.Select(v => v.Code)));
                        continue;
                    }

                    _logger.LogInformation("Trade opened. Strategy={Strategy} Symbol={Symbol} Direction={Dir} Lots={Lots}",
                        strategy.Id, intent.Symbol, intent.Direction, 0.1m);
                }
            }
        }
        catch (OperationCanceledException) { }
        _logger.LogDebug("Tick processor stopped");
    }

    private async Task ProcessBarsAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Bar processor started");
            await foreach (var bar in _broker.BarStream.ReadAllAsync(ct))
            {
                Interlocked.Increment(ref _barCount);
                var byTf = _bars.GetOrAdd(bar.Symbol, _ => new());
                var list = byTf.GetOrAdd(bar.Timeframe, _ => new());
                lock (list) { list.Add(bar); }
                await RecomputeIndicatorsAsync(bar.Symbol, bar.Timeframe);
            }
        }
        catch (OperationCanceledException) { }
        _logger.LogDebug("Bar processor stopped. Total bars accumulated: {Count}",
            _bars.Sum(kv => kv.Value.Sum(tf => tf.Value.Count)));
    }

    private async Task ProcessAccountUpdatesAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Account update processor started");
            await foreach (var update in _broker.AccountStream.ReadAllAsync(ct))
                Interlocked.Exchange(ref _latestAccountUpdate, update);
        }
        catch (OperationCanceledException) { }
        _logger.LogDebug("Account update processor stopped");
    }

    private async Task ProcessExecutionEventsAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogDebug("Execution event processor started");
            await foreach (var evt in _broker.ExecutionStream.ReadAllAsync(ct))
                await _executionEventChannel.Writer.WriteAsync(evt, ct);
        }
        catch (OperationCanceledException) { }
        _logger.LogDebug("Execution event processor stopped");
    }

    private IReadOnlyDictionary<Timeframe, IReadOnlyList<Bar>>? BuildBarSnapshot(Symbol symbol)
    {
        if (!_bars.TryGetValue(symbol, out var byTf))
            return null;

        var snapshot = new Dictionary<Timeframe, IReadOnlyList<Bar>>();
        foreach (var (tf, list) in byTf)
        {
            lock (list) { snapshot[tf] = list.ToList(); }
        }
        return snapshot;
    }

    private async Task RecomputeIndicatorsAsync(Symbol symbol, Timeframe tf)
    {
        if (!_bars.TryGetValue(symbol, out var byTf)) return;
        if (!byTf.TryGetValue(tf, out var list)) return;

        IReadOnlyList<Bar> bars;
        lock (list) { bars = list.ToList(); }

        foreach (var strategy in _strategies)
        {
            var p = (strategy as TrendBreakoutStrategy)?.GetParameters();
            if (p is null) continue;

            _indicatorValues[$"ATR_{p.AtrPeriod}"] = _indicators.Atr(bars, p.AtrPeriod);
            _indicatorValues[$"EMA_{p.MaPeriod}"] = _indicators.Ema(bars, p.MaPeriod);

            _logger.LogTrace("Indicators recomputed. Strategy={Strategy} Bars={Count} ATR={Atr:F6} EMA={Ema:F6}",
                strategy.Id, bars.Count,
                _indicatorValues[$"ATR_{p.AtrPeriod}"],
                _indicatorValues[$"EMA_{p.MaPeriod}"]);
        }

        await Task.CompletedTask;
    }

    private async Task WarmUpIndicatorsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Warm-up: loading indicators for {Count} strategies", _strategies.Count());
        foreach (var strategy in _strategies)
        {
            foreach (var tf in strategy.RequiredTimeframes)
            {
                var symbol = Symbol.Parse("EURUSD");
                var byTf = _bars.GetOrAdd(symbol, _ => new());
                var list = byTf.GetOrAdd(tf, _ => new());

                _logger.LogInformation("Warm-up: strategy={Strategy} timeframe={Tf} bars={Count}",
                    strategy.Id, tf, list.Count);
            }
        }
        await Task.CompletedTask;
    }

    private void HandleExecutionEvent(ExecutionEvent evt)
    {
        _logger.LogDebug("Execution event received. OrderId={OrderId} State={State} FillPrice={FillPrice}",
            evt.OrderId, evt.NewState, evt.FillPrice?.Value);
    }

    private void HandleAccountUpdate(AccountUpdate update)
    {
        _logger.LogTrace("Account update: Balance={Balance} Equity={Equity} FloatingPnl={FloatingPnl}",
            update.Balance, update.Equity, update.FloatingPnL);
    }
}

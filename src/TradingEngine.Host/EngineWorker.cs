using System.Collections.Concurrent;

namespace TradingEngine.Host;

public sealed class EngineWorker : BackgroundService
{
    private readonly IBrokerAdapter _broker;
    private readonly IRiskManager _riskManager;
    private readonly IEnumerable<IStrategy> _strategies;
    private readonly IIndicatorService _indicators;
    private readonly IEventBus _eventBus;
    private readonly IEngineClock _clock;
    private readonly DataFeedService? _dataFeed;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly IRiskProfileResolver _riskProfileResolver;
    private readonly Func<string, string, decimal> _crossRateProvider;
    private readonly PersistenceService _persistence;
    private readonly OrderDispatcher _orderDispatcher;
    private readonly PositionTracker _positionTracker;
    private readonly EngineMode _engineMode;
    private readonly ILogger<EngineWorker> _logger;

    private readonly ConcurrentDictionary<Symbol, ConcurrentDictionary<Timeframe, List<Bar>>> _bars = new();
    private readonly ConcurrentDictionary<string, double> _indicatorValues = new();
    private EquitySnapshot _currentEquity = new(DateTime.MinValue, 0, 0, 0, 0, 0, 0, 0, EngineMode.Backtest);

    private readonly Channel<ExecutionEvent> _executionEventChannel =
        Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true
        });

    private readonly Dictionary<string, double> _reusableIndicatorDict = new();

    private AccountUpdate? _latestAccountUpdate;
    private const int MaxBarHistory = 500;
    private long _tickCount;
    private long _barCount;

    public EngineWorker(
        IBrokerAdapter broker,
        IRiskManager riskManager,
        IEnumerable<IStrategy> strategies,
        IIndicatorService indicators,
        IEventBus eventBus,
        IEngineClock clock,
        ISymbolInfoRegistry symbolRegistry,
        IRiskProfileResolver riskProfileResolver,
        Func<string, string, decimal> crossRateProvider,
        PersistenceService persistence,
        OrderDispatcher orderDispatcher,
        PositionTracker positionTracker,
        ILogger<EngineWorker> logger,
        DataFeedService? dataFeed = null)
    {
        _broker = broker;
        _riskManager = riskManager;
        _strategies = strategies;
        _indicators = indicators;
        _eventBus = eventBus;
        _clock = clock;
        _symbolRegistry = symbolRegistry;
        _riskProfileResolver = riskProfileResolver;
        _crossRateProvider = crossRateProvider;
        _persistence = persistence;
        _orderDispatcher = orderDispatcher;
        _positionTracker = positionTracker;
        _engineMode = _broker is SimulatedBrokerAdapter ? EngineMode.Backtest : EngineMode.Live;
        _dataFeed = dataFeed;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Engine starting. Mode={Mode} Strategies={Count}",
            _engineMode, _strategies.Count());

        await _broker.ConnectAsync(ct);

        try
        {
            var accountState = await _broker.GetAccountStateAsync(ct);
            if (accountState.Balance > 0)
            {
                _riskManager.UpdateEquityLevels(accountState.Equity);
                _logger.LogInformation("Startup reconciliation: Balance={Balance} Equity={Equity} OpenPositions={Count}",
                    accountState.Balance, accountState.Equity, accountState.OpenPositions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Startup reconciliation failed — continuing with clean state");
        }

        await WarmUpIndicatorsAsync(ct);

        var tasks = new[]
        {
            ProcessTicksAsync(ct),
            ProcessBarsAsync(ct),
            ProcessAccountUpdatesAsync(ct),
            ProcessExecutionEventsAsync(ct),
        };

        await Task.WhenAll(tasks);

        _logger.LogInformation("Engine stopped. Ticks={Ticks} Bars={Bars}",
            Interlocked.Read(ref _tickCount), Interlocked.Read(ref _barCount));
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
                    _positionTracker.OnExecution(execEvent, _strategies);

                if (_riskManager.ConsumeForceClosePending())
                {
                    var openPosis = _positionTracker.OpenPositions;
                    _logger.LogCritical("Force-close triggered. Closing {Count} open positions", openPosis.Count);
                    foreach (var (_, pos) in openPosis.ToList())
                        await _broker.ClosePositionAsync(pos.Id, ct);
                }

                var accountUpdate = Interlocked.Exchange(ref _latestAccountUpdate, null);
                if (accountUpdate is not null)
                    HandleAccountUpdate(accountUpdate);

                if (_dataFeed is not null && _broker is SimulatedBrokerAdapter sim)
                    sim.OnTickReceived(tick);

                foreach (var strategy in _strategies)
                {
                    var barSnapshot = BuildBarSnapshot(tick.Symbol);
                    if (barSnapshot is null) continue;
                    if (barSnapshot.Values.Sum(b => b.Count) < strategy.RequiredBarCount) continue;

                    BuildIndicatorSnapshot(tick.Symbol);
                    var context = new MarketContext(tick.Symbol, tick, barSnapshot, _reusableIndicatorDict, _clock.UtcNow);

                    var intent = strategy.Evaluate(context);
                    if (intent is null) continue;

                    var equity = Volatile.Read(ref _currentEquity);

                    var orderCtx = await _orderDispatcher.DispatchAsync(intent, equity, tick.Mid, _broker, ct);
                    if (orderCtx is null) continue;

                    var orderReq = new OrderRequest(intent, orderCtx.Lots, intent.Symbol, intent.Direction, OrderType.Market, intent.LimitPrice);
                    _positionTracker.TrackOrder(orderCtx.OrderId, orderReq, orderCtx.RiskAmount);

                    _logger.LogInformation("Trade opened. Strategy={Strategy} Symbol={Symbol} Dir={Dir} Lots={Lots} Entry={Entry} SL={SL} TP={TP}",
                        strategy.Id, intent.Symbol, intent.Direction, orderCtx.Lots, tick.Mid,
                        intent.StopLoss.Value, intent.TakeProfit?.Value);
                }
            }
        }
        catch (OperationCanceledException) { }
        _logger.LogDebug("Tick processor stopped");
    }

    private void BuildIndicatorSnapshot(Symbol symbol)
    {
        _reusableIndicatorDict.Clear();
        var prefix = $"{symbol}:";
        var prefixLen = prefix.Length;
        foreach (var (key, value) in _indicatorValues)
        {
            if (key.StartsWith(prefix))
                _reusableIndicatorDict[key[prefixLen..]] = value;
        }
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
                lock (list)
                {
                    list.Add(bar);
                    if (list.Count > MaxBarHistory)
                        list.RemoveAt(0);
                }
                await RecomputeIndicatorsAsync(bar.Symbol, bar.Timeframe);
            }
        }
        catch (OperationCanceledException) { }
        _logger.LogDebug("Bar processor stopped");
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
    }

    private void HandleAccountUpdate(AccountUpdate update)
    {
        _riskManager.UpdateEquityLevels(update.Equity);

        var riskState = _riskManager.CurrentState;
        var equity = new EquitySnapshot(
            update.TimestampUtc, update.Balance, update.FloatingPnL, update.Equity,
            update.Equity, update.Equity, riskState.DailyDrawdownUsed, riskState.MaxDrawdownUsed, _engineMode);
        Volatile.Write(ref _currentEquity, equity);
        _ = _persistence.SaveEquitySnapshotAsync(equity, CancellationToken.None);
        _ = _eventBus.PublishAsync(new EquityUpdated(equity, riskState, _clock.UtcNow), CancellationToken.None);
    }

    private IReadOnlyDictionary<Timeframe, IReadOnlyList<Bar>>? BuildBarSnapshot(Symbol symbol)
    {
        if (!_bars.TryGetValue(symbol, out var byTf)) return null;
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
            foreach (var req in strategy.RequiredIndicators)
            {
                var key = $"{symbol}:{req.Key}";
                _indicatorValues[key] = req.Type switch
                {
                    IndicatorType.Atr => _indicators.Atr(bars, req.Period),
                    IndicatorType.Ema => _indicators.Ema(bars, req.Period),
                    IndicatorType.Rsi => _indicators.Rsi(bars, req.Period),
                    IndicatorType.Sma => _indicators.Sma(bars, req.Period),
                    _ => throw new NotSupportedException($"Indicator {req.Type} not supported for recompute")
                };
            }
        }
        await Task.CompletedTask;
    }

    private async Task WarmUpIndicatorsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Warm-up: loading indicators for {Count} strategies", _strategies.Count());
        foreach (var strategy in _strategies)
        {
            _logger.LogInformation("Warm-up: strategy={Strategy} timeframes={Tf} bars required {Count}",
                strategy.Id, string.Join(",", strategy.RequiredTimeframes), strategy.RequiredBarCount);
        }
        await Task.CompletedTask;
    }
}

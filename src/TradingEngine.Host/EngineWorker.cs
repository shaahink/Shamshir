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
    private readonly DrawdownTracker _drawdownTracker;
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
        DrawdownTracker drawdownTracker,
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
        _drawdownTracker = drawdownTracker;
        _engineMode = _broker is SimulatedBrokerAdapter ? EngineMode.Backtest : EngineMode.Live;
        _dataFeed = dataFeed;
        _logger = logger;
    }

    private void ResetState()
    {
        _bars.Clear();
        _indicatorValues.Clear();
        _reusableIndicatorDict.Clear();
        Interlocked.Exchange(ref _latestAccountUpdate, null);
        Volatile.Write(ref _currentEquity, new EquitySnapshot(DateTime.MinValue, 0, 0, 0, 0, 0, 0, 0, _engineMode));
        Interlocked.Exchange(ref _tickCount, 0);
        Interlocked.Exchange(ref _barCount, 0);
        _logger.LogInformation("Engine state reset for new connection");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Engine starting. Mode={Mode} Strategies={Count}",
            _engineMode, _strategies.Count());

        if (_broker is NetMQBrokerAdapter mqAdapter)
            mqAdapter.OnConnected = ResetState;

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
                {
                    _positionTracker.OnExecution(execEvent, _strategies);
                    _logger.LogInformation("EXEC|{OrderId}|{State}|fill={Fill}|lots={Lots}",
                        execEvent.OrderId, execEvent.NewState,
                        execEvent.FillPrice?.Value.ToString("F5") ?? "none",
                        execEvent.FilledLots);
                }

                if (_riskManager.ConsumeForceClosePending())
                {
                    _logger.LogCritical("Force-close triggered. Closing {Count} open positions",
                        _positionTracker.OpenPositions.Count);
                    foreach (var (_, pos) in _positionTracker.OpenPositions.ToList())
                        await _broker.ClosePositionAsync(pos.Id, ct);
                }

                var accountUpdate = Interlocked.Exchange(ref _latestAccountUpdate, null);
                if (accountUpdate is not null)
                    HandleAccountUpdate(accountUpdate);

                if (_dataFeed is not null && _broker is SimulatedBrokerAdapter sim)
                    sim.OnTickReceived(tick);

                _logger.LogDebug("TICK|{Symbol}|{Bid:F5}|{Ask:F5}|{Total}",
                    tick.Symbol.Value, tick.Bid, tick.Ask, Interlocked.Read(ref _tickCount));
            }
        }
        catch (OperationCanceledException) { }
        _logger.LogDebug("Tick processor stopped");
    }

    private async Task ProcessBarsAsync(CancellationToken ct)
    {
        _logger.LogDebug("Bar processor started");
        try
        {
            await foreach (var bar in _broker.BarStream.ReadAllAsync(ct))
            {
                try
                {
                    Interlocked.Increment(ref _barCount);
                    var byTf = _bars.GetOrAdd(bar.Symbol, _ => new());
                    var list = byTf.GetOrAdd(bar.Timeframe, _ => new());
                    int barCount;
                    lock (list)
                    {
                        list.Add(bar);
                        if (list.Count > MaxBarHistory)
                            list.RemoveAt(0);
                        barCount = list.Count;
                    }

                    await RecomputeIndicatorsAsync(bar.Symbol, bar.Timeframe);

                    _logger.LogInformation("BAR_EVAL|{Symbol}|{Tf}|openTime={OpenTime:yyyy-MM-dd HH:mm}|close={Close:F5}|bars={Count}|total={Total}",
                        bar.Symbol.Value, bar.Timeframe, bar.OpenTimeUtc, bar.Close, barCount, Interlocked.Read(ref _barCount));

                    var halfSpread = ResolveHalfSpread(bar.Symbol);
                    var closeTick = new Tick(bar.Symbol, bar.Close, bar.Close + halfSpread,
                        bar.OpenTimeUtc + GetBarDuration(bar.Timeframe));
                    var barSnapshot = BuildBarSnapshot(bar.Symbol);
                    if (barSnapshot is null) continue;

                    BuildIndicatorSnapshot(bar.Symbol);

                    foreach (var strategy in _strategies)
                    {
                        var totalBars = barSnapshot.Values.Sum(b => b.Count);
                        if (totalBars < strategy.RequiredBarCount)
                        {
                            _logger.LogDebug("EVAL|{Strategy}|{Symbol}|NEED_BARS|have={Have}|need={Need}",
                                strategy.Id, bar.Symbol.Value, totalBars, strategy.RequiredBarCount);
                            continue;
                        }

                        var context = new MarketContext(bar.Symbol, closeTick, barSnapshot,
                            _reusableIndicatorDict, _clock.UtcNow);
                        var intent = strategy.Evaluate(context);

                        if (intent is null)
                        {
                            _logger.LogDebug("EVAL|{Strategy}|{Symbol}|NO_SIGNAL", strategy.Id, bar.Symbol.Value);
                            continue;
                        }

                        _logger.LogInformation("SIGNAL|{Strategy}|{Symbol}|{Dir}|sl={SL:F5}|tp={TP}",
                            strategy.Id, bar.Symbol.Value, intent.Direction,
                            intent.StopLoss.Value, intent.TakeProfit?.Value.ToString("F5") ?? "none");
                        _logger.LogInformation("SIGNAL_REASON|{Strategy}|{Reason}", strategy.Id, intent.Reason);

                        var equity = Volatile.Read(ref _currentEquity);
                        if (equity.Balance == 0)
                        {
                            _logger.LogWarning("DISPATCH_SKIP|{Strategy}|{Symbol}|reason=equity not initialized",
                                strategy.Id, bar.Symbol.Value);
                            continue;
                        }
                        var orderCtx = await _orderDispatcher.DispatchAsync(intent, equity, bar.Close, _broker, ct);
                        if (orderCtx is null) continue;

                        var orderReq = new OrderRequest(intent, orderCtx.Lots, intent.Symbol,
                            intent.Direction, OrderType.Market, intent.LimitPrice);
                        _positionTracker.TrackOrder(orderCtx.OrderId, orderReq, orderCtx.RiskAmount);

                        _logger.LogInformation("ORDER|{Strategy}|{OrderId}|{Dir}|lots={Lots}|entry={Entry:F5}",
                            strategy.Id, orderCtx.OrderId, intent.Direction, orderCtx.Lots, bar.Close);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BAR_PROC_ERR|{Symbol}|{OpenTime}", bar.Symbol, bar.OpenTimeUtc);
                }
            }
        }
        catch (OperationCanceledException) { }
        _logger.LogDebug("Bar processor stopped");
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
        _drawdownTracker.InitializeIfNeeded(update.Balance);
        _riskManager.UpdateEquityLevels(update.Equity);

        var riskState = _riskManager.CurrentState;
        var equity = new EquitySnapshot(
            update.TimestampUtc, update.Balance, update.FloatingPnL, update.Equity,
            _drawdownTracker.PeakEquity, _drawdownTracker.DailyStartEquity,
            riskState.DailyDrawdownUsed, riskState.MaxDrawdownUsed, _engineMode);
        Volatile.Write(ref _currentEquity, equity);
        if (_engineMode != EngineMode.Backtest)
            _ = _persistence.SaveEquitySnapshotAsync(equity, CancellationToken.None);
        _ = _eventBus.PublishAsync(new EquityUpdated(equity, riskState, _clock.UtcNow), CancellationToken.None);
        _logger.LogInformation("ACCOUNT|balance={Balance:F2}|equity={Equity:F2}|dd={DD:P1}",
            update.Balance, update.Equity, riskState.DailyDrawdownUsed);
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
                    IndicatorType.BollingerBands => _indicators.BollingerBands(bars, req.Period, req.StdDev).Middle,
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

    private decimal ResolveHalfSpread(Symbol symbol)
    {
        try { return _symbolRegistry.Get(symbol).TypicalSpread / 2m; }
        catch { return 0.00005m; }
    }

    private static TimeSpan GetBarDuration(Timeframe tf) => tf switch
    {
        Timeframe.M1 => TimeSpan.FromMinutes(1),
        Timeframe.M5 => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.M30 => TimeSpan.FromMinutes(30),
        Timeframe.H1 => TimeSpan.FromHours(1),
        Timeframe.H4 => TimeSpan.FromHours(4),
        Timeframe.D1 => TimeSpan.FromDays(1),
        _ => TimeSpan.FromHours(1),
    };
}

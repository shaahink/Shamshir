using System.Collections.Concurrent;

namespace TradingEngine.Host;

public sealed class EngineWorker : BackgroundService
{
    private readonly IBrokerAdapter _broker;
    private readonly IRiskManager _riskManager;
    private readonly IReadOnlyList<IStrategy> _strategies;
    private readonly IIndicatorService _indicators;
    private readonly IEventBus _eventBus;
    private readonly IEngineClock _clock;
    private readonly DataFeedService? _dataFeed;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly IRiskProfileResolver _riskProfileResolver;
    private readonly Func<string, string, decimal> _crossRateProvider;
    private readonly PersistenceService _persistence;
    private readonly IEquitySink? _equitySink;
    private readonly OrderDispatcher _orderDispatcher;
    private readonly PositionTracker _positionTracker;
    private readonly SizingPolicyOptions _sizingPolicy;
    private readonly ITradingGovernor? _governor;
    private readonly ISignalGate? _signalGate;
    private readonly IStrategyBank _strategyBank;
    private readonly IRegimeDetector _regimeDetector;
    private readonly EngineMode _engineMode;
    private readonly EngineRunContext _runContext;
    private readonly CrossRateStore _crossRateStore;
    private readonly ILogger<EngineWorker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IProgress<BacktestProgressEvent>? _progress;
    private readonly IPipelineJournal? _journal;


    public EngineWorker(
        EngineWorkerDependencies deps,
        EngineRunContext runContext,
        ILogger<EngineWorker> logger,
        ILoggerFactory loggerFactory)
    {
        _broker = deps.Market.Broker;
        _riskManager = deps.Risk.RiskManager;
        _strategies = deps.Strategies.Strategies.ToList();
        _indicators = deps.Market.Indicators;
        _indicatorSnapshot = new IndicatorSnapshotService(_indicators, _strategies);
        _marketEvents = new MarketEventSource(_broker, _executionEventChannel, logger);
        _eventBus = deps.Persistence.EventBus;
        _clock = deps.Market.Clock;
        _symbolRegistry = deps.Market.SymbolRegistry;
        _riskProfileResolver = deps.Risk.RiskProfileResolver;
        _crossRateProvider = deps.Risk.CrossRateProvider;
        _persistence = deps.Persistence.Persistence;
        _equitySink = deps.Persistence.EquitySink;
        _orderDispatcher = deps.Strategies.OrderDispatcher;
        _positionTracker = deps.Strategies.PositionTracker;
        _sizingPolicy = deps.Risk.SizingPolicy;
        _governor = deps.Risk.Governor;
        _signalGate = deps.Strategies.SignalGate;
        _strategyBank = deps.Strategies.StrategyBank;
        _regimeDetector = deps.Strategies.RegimeDetector;
        _runContext = runContext;
        _crossRateStore = deps.Market.CrossRateStore;
        _engineMode = deps.Market.EngineMode;
        _dataFeed = deps.Market.DataFeed;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _progress = deps.Persistence.Progress;
        _journal = deps.Persistence.Journal;
        _accountProcessor = new AccountProcessor(_riskManager, _positionTracker, _sizingPolicy,
            _eventBus, _clock, _engineMode, _crossRateStore, _equitySink,
            e => Volatile.Write(ref _currentEquity, e), logger);
    }

    private readonly IndicatorSnapshotService _indicatorSnapshot;
    private readonly MarketEventSource _marketEvents;
    private readonly AccountProcessor _accountProcessor;

    private EquitySnapshot _currentEquity = new(DateTime.MinValue, 0, 0, 0, 0, 0, 0, 0, EngineMode.Backtest);

    private readonly Channel<ExecutionEvent> _executionEventChannel =
        Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true
        });

    private long _tickCount;
    private long _barCount;

    private readonly Dictionary<(Symbol, Timeframe), MarketRegime> _currentRegimes = new();

    private void ResetState()
    {
        _indicatorSnapshot.Reset();
        Volatile.Write(ref _currentEquity, new EquitySnapshot(DateTime.MinValue, 0, 0, 0, 0, 0, 0, 0, _engineMode));
        Interlocked.Exchange(ref _tickCount, 0);
        Interlocked.Exchange(ref _barCount, 0);
        _logger.LogInformation("Engine state reset for new connection");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Engine starting. Mode={Mode} Strategies={Count}",
            _engineMode, _strategies.Count);

        if (_signalGate is not null)
        {
            foreach (var s in _strategies)
                _signalGate.RegisterStrategy(s.Config);
        }

        if (_broker is CTraderBrokerAdapter mqAdapter)
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

        await _indicatorSnapshot.WarmUpIndicatorsAsync(ct);

        if (_engineMode == EngineMode.Backtest)
        {
            await RunBacktestLoopAsync(ct);
        }
        else
        {
            await Task.WhenAll(
                ProcessTicksAsync(ct),
                ProcessBarsAsync(ct),
                _marketEvents.ProcessAccountUpdatesAsync(ct),
            _marketEvents.ProcessExecutionEventsAsync(ct),
            _marketEvents.ProcessAccountQueueAsync(ct, _accountProcessor.HandleAsync));
        }

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
                    await _positionTracker.OnExecutionAsync(execEvent, _strategies);
                    var state = execEvent.NewState;
                    _logger.LogInformation("EXEC|{OrderId}|{State}|fill={Fill}|lots={Lots}",
                        execEvent.OrderId, state,
                        execEvent.FillPrice?.Value.ToString("F5") ?? "none",
                        execEvent.FilledLots);

                    _progress?.Report(new BacktestProgressEvent(
                        _runContext.RunId, state == OrderState.Rejected ? "REJECTED" : "EXEC",
                        $"EXEC {execEvent.OrderId} [{state}] fill={execEvent.FillPrice?.Value.ToString("F5") ?? "none"} lots={execEvent.FilledLots}{(state == OrderState.Rejected ? " reason=" + (execEvent.RejectionReason ?? "?") : "")}",
                        _clock.UtcNow));
                }

                if (_dataFeed is not null && _broker is SimulatedBrokerAdapter sim)
                {
                    sim.OnTickReceived(tick);
                }

                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("TICK|{Symbol}|{Bid:F5}|{Ask:F5}|{Total}",
                        tick.Symbol.Value, tick.Bid, tick.Ask, Interlocked.Read(ref _tickCount));
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            while (_executionEventChannel.Reader.TryRead(out var execEvent))
                await _positionTracker.OnExecutionAsync(execEvent, _strategies);
        }
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
                    await ProcessSingleBarAsync(bar, ct);
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

    /// <summary>
    /// The single canonical per-bar trading body shared by both the live path
    /// (<see cref="ProcessBarsAsync"/>) and the backtest path
    /// (<see cref="RunBacktestLoopAsync"/>): indicator recompute → bar snapshot → regime →
    /// active strategies → evaluate → signal gate → dispatch → track → drain executions.
    /// </summary>
    private async Task ProcessSingleBarAsync(Bar bar, CancellationToken ct)
    {
                {
                    Interlocked.Increment(ref _barCount);
                    var byTf = _indicatorSnapshot.Bars.GetOrAdd(bar.Symbol, _ => new());
                    var list = byTf.GetOrAdd(bar.Timeframe, _ => new());
                    int barCount;
                    lock (list)
                    {
                        list.Add(bar);
                        if (list.Count > 500)
                            list.RemoveAt(0);
                        barCount = list.Count;
                    }

                    await _indicatorSnapshot.RecomputeIndicatorsAsync(bar.Symbol, bar.Timeframe, ct);

                    _logger.LogDebug("BAR_EVAL|{Symbol}|{Tf}|openTime={OpenTime:yyyy-MM-dd HH:mm}|close={Close:F5}|bars={Count}|total={Total}",
                        bar.Symbol.Value, bar.Timeframe, bar.OpenTimeUtc, bar.Close, barCount, Interlocked.Read(ref _barCount));

                    _progress?.Report(new BacktestProgressEvent(
                        _runContext.RunId, "BAR",
                        $"Bar {bar.OpenTimeUtc:yyyy-MM-dd HH:mm} | close={bar.Close:F5} | total={Interlocked.Read(ref _barCount)}",
                        _clock.UtcNow));

                    var halfSpread = ResolveHalfSpread(bar.Symbol);
                    var closeTick = new Tick(bar.Symbol, bar.Close, bar.Close + halfSpread,
                        bar.OpenTimeUtc + GetBarDuration(bar.Timeframe));
                    var barSnapshot = _indicatorSnapshot.BuildBarSnapshot(bar.Symbol);
                    if (barSnapshot is null) return;

                    _indicatorSnapshot.BuildSharedIndicatorSnapshot(bar.Symbol);

                    _signalGate?.OnBar(bar.OpenTimeUtc);

                    var regime = _regimeDetector.Detect(bar.Symbol,
                        barSnapshot[bar.Timeframe],
                        _indicatorSnapshot.ReusableIndicatorDict);
                    _currentRegimes[(bar.Symbol, bar.Timeframe)] = regime;
                    var activeStrategies = _strategyBank.GetActive(bar.Symbol, bar.Timeframe, regime);

                    foreach (var strategy in activeStrategies)
                    {
                        var totalBars = barSnapshot.Values.Sum(b => b.Count);
                        var strategyIndicators = _indicatorSnapshot.BuildStrategyIndicatorValues(bar.Symbol, strategy);

                        if (totalBars < strategy.RequiredBarCount)
                        {
                            _logger.LogDebug("EVAL|{Strategy}|{Symbol}|NEED_indicatorSnapshot.Bars|have={Have}|need={Need}",
                                strategy.Id, bar.Symbol.Value, totalBars, strategy.RequiredBarCount);
                            _ = _eventBus.PublishAsync(new BarEvaluated(
                                _runContext.RunId, bar.Symbol, bar.Timeframe, bar.OpenTimeUtc,
                                strategy.Id, new Dictionary<string, double>(strategyIndicators),
                                false, null, $"not enough bars (have {totalBars}, need {strategy.RequiredBarCount})",
                                _clock.UtcNow), CancellationToken.None);
                            continue;
                        }

                        var context = new MarketContext(bar.Symbol, closeTick, barSnapshot,
                            strategyIndicators, _clock.UtcNow);
                        var intent = strategy.Evaluate(context);

                        _ = _eventBus.PublishAsync(new BarEvaluated(
                            _runContext.RunId, bar.Symbol, bar.Timeframe, bar.OpenTimeUtc,
                            strategy.Id, new Dictionary<string, double>(strategyIndicators),
                            intent is not null, intent?.Direction,
                            intent?.Reason ?? "no signal",
                            _clock.UtcNow), CancellationToken.None);

                        if (intent is null)
                        {
                            _logger.LogDebug("EVAL|{Strategy}|{Symbol}|NO_SIGNAL", strategy.Id, bar.Symbol.Value);
                            continue;
                        }

                        if (_signalGate is not null)
                        {
                            var gateResult = _signalGate.Check(strategy.Id, intent.Symbol.Value, intent.Direction, bar.OpenTimeUtc);
                            if (!gateResult.Allowed)
                            {
                                _logger.LogInformation("SIGNAL_GATED|{Strategy}|{Symbol}|{Reason}", strategy.Id, intent.Symbol.Value, gateResult.Reason);
                                _ = _eventBus.PublishAsync(new BarEvaluated(
                                    _runContext.RunId, bar.Symbol, bar.Timeframe, bar.OpenTimeUtc,
                                    strategy.Id, new Dictionary<string, double>(strategyIndicators),
                                    false, intent.Direction,
                                    $"REENTRY:{gateResult.Reason}",
                                    _clock.UtcNow), CancellationToken.None);
                                continue;
                            }
                        }

                        _logger.LogInformation("SIGNAL|{Strategy}|{Symbol}|{Dir}|sl={SL:F5}|tp={TP}",
                            strategy.Id, bar.Symbol.Value, intent.Direction,
                            intent.StopLoss.Value, intent.TakeProfit?.Value.ToString("F5") ?? "none");
                        _logger.LogInformation("SIGNAL_REASON|{Strategy}|{Reason}", strategy.Id, intent.Reason);

                        _progress?.Report(new BacktestProgressEvent(
                            _runContext.RunId, "SIGNAL",
                            $"SIGNAL {strategy.Id} {intent.Direction} sl={intent.StopLoss.Value:F5} tp={intent.TakeProfit?.Value.ToString("F5") ?? "none"} reason={intent.Reason}",
                            _clock.UtcNow));

                        var equity = Volatile.Read(ref _currentEquity);
                        if (equity.Balance == 0)
                        {
                            _logger.LogWarning("DISPATCH_SKIP|{Strategy}|{Symbol}|reason=equity not initialized",
                                strategy.Id, bar.Symbol.Value);
                            continue;
                        }
                        var orderCtx = await _orderDispatcher.DispatchAsync(intent, equity, bar.Close, _broker, [], ct);
                        if (orderCtx is null) continue;

                        var orderReq = new OrderRequest(intent, orderCtx.Lots, intent.Symbol,
                            intent.Direction, OrderType.Market, intent.LimitPrice);
                        _positionTracker.TrackOrder(orderCtx.OrderId, orderReq, orderCtx.RiskAmount);

                        _logger.LogInformation("ORDER|{Strategy}|{OrderId}|{Dir}|lots={Lots}|entry={Entry:F5}",
                            strategy.Id, orderCtx.OrderId, intent.Direction, orderCtx.Lots, bar.Close);

                        _progress?.Report(new BacktestProgressEvent(
                            _runContext.RunId, "ORDER",
                            $"ORDER {strategy.Id} {intent.Direction} lots={orderCtx.Lots:F2} entry?{bar.Close:F5}",
                            _clock.UtcNow));
                    }

                    _journal?.Write("BAR_EVAL", bar.Symbol.Value, bar.OpenTimeUtc);

                    await _marketEvents.DrainExecutionStreamAsync(_positionTracker, _strategies, _progress, _runContext.RunId, _clock);
                }
    }





    private async Task RunBacktestLoopAsync(CancellationToken ct)
    {
        var initAcct = await _broker.AccountStream.ReadAsync(ct);
        await _accountProcessor.HandleAsync(initAcct);

        _logger.LogDebug("Backtest loop started");
        try
        {
            await foreach (var bar in _broker.BarStream.ReadAllAsync(ct))
            {
                try
                {
                    if (_broker is BacktestReplayAdapter replay)
                        replay.SyncToBar(bar.Close, bar.OpenTimeUtc);

                    UpdateCrossRates(bar);

                    // Pump every account update through the same processor the live path uses,
                    // so equity, the breach watchdog and daily/weekly/monthly resets all run.
                    while (_broker.AccountStream.TryRead(out var acctUpdate))
                        await _accountProcessor.HandleAsync(acctUpdate);

                    await ProcessSingleBarAsync(bar, ct);

                    // Venue concern: a live broker fills SL/TP server-side; in backtest we
                    // simulate it against the bar range, then drain the resulting fills.
                    await SimulateBarExitsAsync(bar, ct);

                    if (_broker is CTraderBrokerAdapter netMq)
                        await _broker.CompleteBarAsync(netMq.CurrentBarSeq, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "BAR_PROC_ERR|{Symbol}|{OpenTime}", bar.Symbol, bar.OpenTimeUtc);
                }
            }
        }
        catch (OperationCanceledException) { }
        _logger.LogDebug("Backtest loop stopped");
    }

    private async Task SimulateBarExitsAsync(Bar bar, CancellationToken ct)
    {
        foreach (var (orderId, pos) in _positionTracker.OpenPositions.ToList())
        {
            if (pos.Symbol != bar.Symbol) continue;
            bool exit;
            if (pos.Direction == TradeDirection.Long)
            {
                exit = bar.Low <= pos.CurrentStopLoss.Value
                    || (pos.TakeProfit is not null && bar.High >= pos.TakeProfit.Value.Value);
            }
            else
            {
                exit = bar.High >= pos.CurrentStopLoss.Value
                    || (pos.TakeProfit is not null && bar.Low <= pos.TakeProfit.Value.Value);
            }

            if (exit)
            {
                _logger.LogInformation("BAR_EXIT|{Id}|{Symbol}|sl={SL:F5}|tp={TP}|low={Low:F5}|high={High:F5}",
                    orderId, pos.Symbol, pos.CurrentStopLoss.Value,
                    pos.TakeProfit?.Value ?? 0, bar.Low, bar.High);
                await _broker.ClosePositionAsync(orderId, ct);
            }
        }

        await _marketEvents.DrainExecutionStreamAsync(_positionTracker, _strategies, _progress, _runContext.RunId, _clock);
    }

    private void UpdateCrossRates(Bar bar)
    {
        if (bar.Symbol.Value == "GBPUSD") _crossRateStore.GbpUsdRate = bar.Close;
        else if (bar.Symbol.Value == "USDJPY") _crossRateStore.UsdJpyRate = bar.Close;
    }

    private decimal ResolveHalfSpread(Symbol symbol)
    {
        try { return _symbolRegistry.Get(symbol).TypicalSpread / 2m; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ResolveHalfSpread failed for {Symbol} — using fallback 0.5pip", symbol);
            return 0.00005m;
        }
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

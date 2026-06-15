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
    private BacktestDriver? _backtestDriver;


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
    }

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

    private int _lastResetIsoWeek = -1;
    private int _lastResetMonth = -1;
    private int _lastResetDayOfYear = -1;

    private readonly Dictionary<(Symbol, Timeframe), MarketRegime> _currentRegimes = new();

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

        await WarmUpIndicatorsAsync(ct);

        if (_engineMode == EngineMode.Backtest)
        {
            await RunBacktestLoopAsync(ct);
        }
        else
        {
            await Task.WhenAll(
                ProcessTicksAsync(ct),
                ProcessBarsAsync(ct),
                ProcessAccountUpdatesAsync(ct),
                ProcessExecutionEventsAsync(ct));
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

                    var accountUpdate = Interlocked.Exchange(ref _latestAccountUpdate, null);
                if (accountUpdate is not null)
                    await HandleAccountUpdateAsync(accountUpdate);

                if (_dataFeed is not null && _broker is SimulatedBrokerAdapter sim)
                    sim.OnTickReceived(tick);

                _logger.LogDebug("TICK|{Symbol}|{Bid:F5}|{Ask:F5}|{Total}",
                    tick.Symbol.Value, tick.Bid, tick.Ask, Interlocked.Read(ref _tickCount));
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

                    await RecomputeIndicatorsAsync(bar.Symbol, bar.Timeframe, ct);

                    _logger.LogDebug("BAR_EVAL|{Symbol}|{Tf}|openTime={OpenTime:yyyy-MM-dd HH:mm}|close={Close:F5}|bars={Count}|total={Total}",
                        bar.Symbol.Value, bar.Timeframe, bar.OpenTimeUtc, bar.Close, barCount, Interlocked.Read(ref _barCount));

                    _progress?.Report(new BacktestProgressEvent(
                        _runContext.RunId, "BAR",
                        $"Bar {bar.OpenTimeUtc:yyyy-MM-dd HH:mm} | close={bar.Close:F5} | total={Interlocked.Read(ref _barCount)}",
                        _clock.UtcNow));

                    var halfSpread = ResolveHalfSpread(bar.Symbol);
                    var closeTick = new Tick(bar.Symbol, bar.Close, bar.Close + halfSpread,
                        bar.OpenTimeUtc + GetBarDuration(bar.Timeframe));
                    var barSnapshot = BuildBarSnapshot(bar.Symbol);
                    if (barSnapshot is null) continue;

                    BuildSharedIndicatorSnapshot(bar.Symbol);

                    _signalGate?.OnBar(bar.OpenTimeUtc);

                    var regime = _regimeDetector.Detect(bar.Symbol,
                        barSnapshot[bar.Timeframe],
                        _reusableIndicatorDict);
                    _currentRegimes[(bar.Symbol, bar.Timeframe)] = regime;
                    var activeStrategies = _strategyBank.GetActive(bar.Symbol, bar.Timeframe, regime);

                    foreach (var strategy in activeStrategies)
                    {
                        var totalBars = barSnapshot.Values.Sum(b => b.Count);
                        var strategyIndicators = BuildStrategyIndicatorValues(bar.Symbol, strategy);

                        if (totalBars < strategy.RequiredBarCount)
                        {
                            _logger.LogDebug("EVAL|{Strategy}|{Symbol}|NEED_BARS|have={Have}|need={Need}",
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

                    await DrainExecutionStreamAsync();

                    // Per-bar SL/TP evaluation removed from live path — belongs in RunBacktestLoopAsync.
                    // Live broker manages orders server-side; engine must not second-guess SL/TP.
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

    private void BuildSharedIndicatorSnapshot(Symbol symbol)
    {
        _reusableIndicatorDict.Clear();
        foreach (var strategy in _strategies)
        {
            foreach (var req in strategy.RequiredIndicators)
            {
                var sigKey = IndicatorCache.BuildKey(symbol, req);
                if (_indicatorValues.TryGetValue(sigKey, out var val))
                    _reusableIndicatorDict[req.Key] = val;
                if (_indicatorValues.TryGetValue($"{sigKey}_Upper", out var upper))
                    _reusableIndicatorDict[$"{req.Key}_Upper"] = upper;
                if (_indicatorValues.TryGetValue($"{sigKey}_Lower", out var lower))
                    _reusableIndicatorDict[$"{req.Key}_Lower"] = lower;
                if (_indicatorValues.TryGetValue($"{sigKey}_Signal", out var signal))
                    _reusableIndicatorDict[$"{req.Key}_Signal"] = signal;
                if (_indicatorValues.TryGetValue($"{sigKey}_Histogram", out var hist))
                    _reusableIndicatorDict[$"{req.Key}_Histogram"] = hist;
                if (_indicatorValues.TryGetValue($"{sigKey}_Direction", out var dir))
                    _reusableIndicatorDict[$"{req.Key}_Direction"] = dir;
            }
        }
    }

    private IReadOnlyDictionary<string, double> BuildStrategyIndicatorValues(Symbol symbol, IStrategy strategy)
    {
        var dict = new Dictionary<string, double>();
        foreach (var req in strategy.RequiredIndicators)
        {
            var sigKey = IndicatorCache.BuildKey(symbol, req);
            if (_indicatorValues.TryGetValue(sigKey, out var val))
                dict[req.Key] = val;
            if (_indicatorValues.TryGetValue($"{sigKey}_Upper", out var upper))
                dict[$"{req.Key}_Upper"] = upper;
            if (_indicatorValues.TryGetValue($"{sigKey}_Lower", out var lower))
                dict[$"{req.Key}_Lower"] = lower;
            if (_indicatorValues.TryGetValue($"{sigKey}_Signal", out var signal))
                dict[$"{req.Key}_Signal"] = signal;
            if (_indicatorValues.TryGetValue($"{sigKey}_Histogram", out var hist))
                dict[$"{req.Key}_Histogram"] = hist;
            if (_indicatorValues.TryGetValue($"{sigKey}_Direction", out var dir))
                dict[$"{req.Key}_Direction"] = dir;
        }
        return dict;
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

    private async Task RunBacktestLoopAsync(CancellationToken ct)
    {
        var initAcct = await _broker.AccountStream.ReadAsync(ct);
        await HandleAccountUpdateAsync(initAcct);

        _backtestDriver = new BacktestDriver(
            _broker, _positionTracker, _orderDispatcher, _riskManager,
            _governor, _signalGate, _strategies, _strategyBank, _regimeDetector,
            _indicators, _eventBus, _progress, _journal, _clock,
            _crossRateStore, _runContext, _executionEventChannel,
            _loggerFactory.CreateLogger<BacktestDriver>());

        await _backtestDriver.RunAsync(ct);
        _logger.LogDebug("Backtest loop stopped");
    }

    private async Task DrainExecutionStreamAsync()
    {
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
        while (_broker.ExecutionStream.TryRead(out var execEvent))
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
    }

    private async Task HandleAccountUpdateAsync(AccountUpdate update)
    {
        _riskManager.InitializeDrawdownIfNeeded(update.Balance);
        _riskManager.UpdateEquityLevels(update.Equity);

        var ruleSet = _riskManager.ActiveRuleSet;
        if (ruleSet is not null && !_riskManager.CurrentState.InProtectionMode)
        {
            var flattenFraction = (decimal)_sizingPolicy.FlattenAtFraction;

            if (_riskManager.CurrentState.DailyDrawdownUsed >= (decimal)ruleSet.MaxDailyLossPercent * flattenFraction)
            {
                _riskManager.EnterProtectionMode(
                    $"Daily DD at {_riskManager.CurrentState.DailyDrawdownUsed:P1} >= {ruleSet.MaxDailyLossPercent * (double)_sizingPolicy.FlattenAtFraction:P1} hard limit",
                    ProtectionCause.DailyDrawdown);
                _logger.LogCritical("BREACH_WATCHDOG: Entered protection mode — daily DD");
                await _positionTracker.RequestForceCloseAllAsync("DailyDD");
            }
            else if (_riskManager.CurrentState.MaxDrawdownUsed >= (decimal)ruleSet.MaxTotalLossPercent * flattenFraction)
            {
                _riskManager.EnterProtectionMode(
                    $"Max DD at {_riskManager.CurrentState.MaxDrawdownUsed:P1} >= {ruleSet.MaxTotalLossPercent * (double)_sizingPolicy.FlattenAtFraction:P1} hard limit",
                    ProtectionCause.MaxDrawdown);
                _logger.LogCritical("BREACH_WATCHDOG: Entered protection mode — max DD");
                await _positionTracker.RequestForceCloseAllAsync("MaxDD");
            }
        }

        var now = _clock.UtcNow;
        var isoWeek = ISOWeek.GetWeekOfYear(now);
        var month = now.Month;
        var dailyKey = now.Year * 1000 + now.DayOfYear;

        if (dailyKey != _lastResetDayOfYear)
        {
            _lastResetDayOfYear = dailyKey;
            _riskManager.OnDailyReset(update.Equity);
            _ = _eventBus.PublishAsync(new DayRolled(now), CancellationToken.None);
        }
        if (isoWeek != _lastResetIsoWeek)
        {
            _lastResetIsoWeek = isoWeek;
            _riskManager.OnWeeklyReset(update.Equity);
            _ = _eventBus.PublishAsync(new WeekRolled(now), CancellationToken.None);
            _ = _eventBus.PublishAsync(new DayRolled(now), CancellationToken.None);
            _ = _eventBus.PublishAsync(new WeeklyEquitySnapshotTaken(
                new EquitySnapshot(update.TimestampUtc, update.Balance, update.FloatingPnL, update.Equity,
                    _riskManager.Drawdown.PeakEquity, _riskManager.Drawdown.DailyStartEquity,
                    _riskManager.CurrentState.WeeklyDrawdownUsed, _riskManager.CurrentState.MaxDrawdownUsed, _engineMode),
                _riskManager.CurrentState, _clock.UtcNow), CancellationToken.None);
        }
        if (month != _lastResetMonth)
        {
            _lastResetMonth = month;
            _riskManager.OnMonthlyReset(update.Equity);
            _ = _eventBus.PublishAsync(new MonthlyEquitySnapshotTaken(
                new EquitySnapshot(update.TimestampUtc, update.Balance, update.FloatingPnL, update.Equity,
                    _riskManager.Drawdown.PeakEquity, _riskManager.Drawdown.DailyStartEquity,
                    _riskManager.CurrentState.MonthlyDrawdownUsed, _riskManager.CurrentState.MaxDrawdownUsed, _engineMode),
                _riskManager.CurrentState, _clock.UtcNow), CancellationToken.None);
        }

        var riskState = _riskManager.CurrentState;
        var equity = new EquitySnapshot(
            update.TimestampUtc, update.Balance, update.FloatingPnL, update.Equity,
            _riskManager.Drawdown.PeakEquity, _riskManager.Drawdown.DailyStartEquity,
            riskState.DailyDrawdownUsed, riskState.MaxDrawdownUsed, _engineMode);
        Volatile.Write(ref _currentEquity, equity);
        if (_equitySink is not null)
        {
            _equitySink.Observe(new AccountSnapshot(
                update.TimestampUtc, update.Balance, update.Equity, update.FloatingPnL,
                _riskManager.Drawdown.PeakEquity, _riskManager.Drawdown.DailyStartEquity,
                riskState.DailyDrawdownUsed, riskState.MaxDrawdownUsed,
                _positionTracker.OpenPositions.Count));
        }
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

    private Task RecomputeIndicatorsAsync(Symbol symbol, Timeframe tf, CancellationToken ct)
    {
        if (!_bars.TryGetValue(symbol, out var byTf)) return Task.CompletedTask;
        if (!byTf.TryGetValue(tf, out var list)) return Task.CompletedTask;

        IReadOnlyList<Bar> bars;
        lock (list) { bars = list.ToList(); }

        foreach (var strategy in _strategies)
        {
            foreach (var req in strategy.RequiredIndicators)
            {
                // Use requested timeframe's bars when different from current tf
                var reqBars = bars;
                if (req.Timeframe != tf && req.Timeframe != default)
                {
                    if (byTf.TryGetValue(req.Timeframe, out var reqList))
                        lock (reqList) { reqBars = reqList.ToList(); }
                    else
                        continue;
                }

                var sigKey = IndicatorCache.BuildKey(symbol, req);
                switch (req.Type)
                {
                    case IndicatorType.Atr:
                        _indicatorValues[sigKey] = _indicators.Atr(reqBars, req.Period);
                        break;
                    case IndicatorType.Ema:
                        _indicatorValues[sigKey] = _indicators.Ema(reqBars, req.Period);
                        break;
                    case IndicatorType.Rsi:
                        _indicatorValues[sigKey] = _indicators.Rsi(reqBars, req.Period);
                        break;
                    case IndicatorType.Sma:
                        _indicatorValues[sigKey] = _indicators.Sma(reqBars, req.Period);
                        break;
                    case IndicatorType.Adx:
                        _indicatorValues[sigKey] = _indicators.Adx(reqBars, req.Period);
                        break;
                    case IndicatorType.BollingerBands:
                        var (upper, middle, lower) = _indicators.BollingerBands(reqBars, req.Period, req.StdDev);
                        _indicatorValues[sigKey] = middle;
                        _indicatorValues[$"{sigKey}_Upper"] = upper;
                        _indicatorValues[$"{sigKey}_Lower"] = lower;
                        break;
                    case IndicatorType.Macd:
                        var macdFast = req.Period;
                        var macdSlow = req.Param1 > 0 ? req.Param1 : 26;
                        var macdSig = (int)(req.Param2 > 0 ? req.Param2 : 9);
                        var macd = _indicators.Macd(reqBars, macdFast, macdSlow, macdSig);
                        _indicatorValues[sigKey] = macd.MacdLine;
                        _indicatorValues[$"{sigKey}_Signal"] = macd.Signal;
                        _indicatorValues[$"{sigKey}_Histogram"] = macd.Histogram;
                        break;
                    case IndicatorType.SuperTrend:
                        var stMult = req.Param2 > 0 ? req.Param2 : 3.0;
                        var st = _indicators.SuperTrend(reqBars, req.Period, stMult);
                        _indicatorValues[sigKey] = st.Line;
                        _indicatorValues[$"{sigKey}_Direction"] = st.Direction;
                        break;
                }
            }
        }
        return Task.CompletedTask;
    }

    private Task WarmUpIndicatorsAsync(CancellationToken ct)
    {
        _logger.LogInformation("Warm-up: loading indicators for {Count} strategies", _strategies.Count);
        foreach (var strategy in _strategies)
        {
            _logger.LogInformation("Warm-up: strategy={Strategy} timeframes={Tf} bars required {Count}",
                strategy.Id, string.Join(",", strategy.RequiredTimeframes), strategy.RequiredBarCount);
        }
        return Task.CompletedTask;
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

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Host;

public sealed class BacktestDriver
{
    private readonly IBrokerAdapter _broker;
    private readonly PositionTracker _positionTracker;
    private readonly OrderDispatcher _orderDispatcher;
    private readonly IRiskManager _riskManager;
    private readonly ITradingGovernor? _governor;
    private readonly ISignalGate? _signalGate;
    private readonly IReadOnlyList<IStrategy> _strategies;
    private readonly IStrategyBank _strategyBank;
    private readonly IRegimeDetector _regimeDetector;
    private readonly IIndicatorService _indicators;
    private readonly IEventBus _eventBus;
    private readonly IProgress<BacktestProgressEvent>? _progress;
    private readonly IPipelineJournal? _journal;
    private readonly IEngineClock _clock;
    private readonly CrossRateStore _crossRateStore;
    private readonly EngineRunContext _runContext;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    private readonly Channel<ExecutionEvent> _executionEventChannel;
    private readonly ConcurrentDictionary<Symbol, ConcurrentDictionary<Timeframe, List<Bar>>> _bars = new();
    private readonly ConcurrentDictionary<string, double> _indicatorValues = new();
    private readonly Dictionary<string, double> _reusableIndicatorDict = new();
    private readonly Dictionary<(Symbol, Timeframe), MarketRegime> _currentRegimes = new();
    private long _barCount;
    private const int MaxBarHistory = 500;

    public BacktestDriver(
        IBrokerAdapter broker,
        PositionTracker positionTracker,
        OrderDispatcher orderDispatcher,
        IRiskManager riskManager,
        ITradingGovernor? governor,
        ISignalGate? signalGate,
        IReadOnlyList<IStrategy> strategies,
        IStrategyBank strategyBank,
        IRegimeDetector regimeDetector,
        IIndicatorService indicators,
        IEventBus eventBus,
        IProgress<BacktestProgressEvent>? progress,
        IPipelineJournal? journal,
        IEngineClock clock,
        CrossRateStore crossRateStore,
        EngineRunContext runContext,
        Channel<ExecutionEvent> executionEventChannel,
        Microsoft.Extensions.Logging.ILogger<BacktestDriver> logger)
    {
        _broker = broker;
        _positionTracker = positionTracker;
        _orderDispatcher = orderDispatcher;
        _riskManager = riskManager;
        _governor = governor;
        _signalGate = signalGate;
        _strategies = strategies;
        _strategyBank = strategyBank;
        _regimeDetector = regimeDetector;
        _indicators = indicators;
        _eventBus = eventBus;
        _progress = progress;
        _journal = journal;
        _clock = clock;
        _crossRateStore = crossRateStore;
        _runContext = runContext;
        _executionEventChannel = executionEventChannel;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
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

                    if (_broker.AccountStream.TryRead(out var acctUpdate))
                        HandleAccountUpdate(acctUpdate);

                    _governor?.OnBar(bar.OpenTimeUtc);
                    _signalGate?.OnBar(bar.OpenTimeUtc);

                    Interlocked.Increment(ref _barCount);
                    var byTf = _bars.GetOrAdd(bar.Symbol, _ => new());
                    var list = byTf.GetOrAdd(bar.Timeframe, _ => new());
                    int barCount;
                    lock (list)
                    {
                        list.Add(bar);
                        if (list.Count > MaxBarHistory) list.RemoveAt(0);
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

                    BuildIndicatorSnapshot(bar.Symbol);

                    var btRegime = _regimeDetector.Detect(bar.Symbol,
                        barSnapshot[bar.Timeframe],
                        _reusableIndicatorDict);
                    _currentRegimes[(bar.Symbol, bar.Timeframe)] = btRegime;
                    var btActiveStrategies = _strategyBank.GetActive(bar.Symbol, bar.Timeframe, btRegime);

                    foreach (var strategy in btActiveStrategies)
                    {
                        var totalBars = barSnapshot.Values.Sum(b => b.Count);
                        if (totalBars < strategy.RequiredBarCount)
                        {
                            _logger.LogDebug("EVAL|{Strategy}|{Symbol}|NEED_BARS|have={Have}|need={Need}",
                                strategy.Id, bar.Symbol.Value, totalBars, strategy.RequiredBarCount);
                            _ = _eventBus.PublishAsync(new BarEvaluated(
                                _runContext.RunId, bar.Symbol, bar.Timeframe, bar.OpenTimeUtc,
                                strategy.Id, new Dictionary<string, double>(_reusableIndicatorDict),
                                false, null, $"not enough bars (have {totalBars}, need {strategy.RequiredBarCount})",
                                _clock.UtcNow), CancellationToken.None);
                            continue;
                        }

                        var context = new MarketContext(bar.Symbol, closeTick, barSnapshot,
                            _reusableIndicatorDict, _clock.UtcNow);
                        var intent = strategy.Evaluate(context);

                        _ = _eventBus.PublishAsync(new BarEvaluated(
                            _runContext.RunId, bar.Symbol, bar.Timeframe, bar.OpenTimeUtc,
                            strategy.Id, new Dictionary<string, double>(_reusableIndicatorDict),
                            intent is not null, intent?.Direction,
                            intent?.Reason ?? "no signal",
                            _clock.UtcNow), CancellationToken.None);

                        if (intent is null)
                        {
                            _logger.LogDebug("EVAL|{Strategy}|{Symbol}|NO_SIGNAL", strategy.Id, bar.Symbol.Value);
                            continue;
                        }

                        _logger.LogInformation("SIGNAL|{Strategy}|{Symbol}|{Dir}|sl={SL:F5}|tp={TP}",
                            strategy.Id, bar.Symbol.Value, intent.Direction,
                            intent.StopLoss.Value, intent.TakeProfit?.Value.ToString("F5") ?? "none");
                        _logger.LogInformation("SIGNAL_REASON|{Strategy}|{Reason}", strategy.Id, intent.Reason);

                        _progress?.Report(new BacktestProgressEvent(
                            _runContext.RunId, "SIGNAL",
                            $"SIGNAL {strategy.Id} {intent.Direction} sl={intent.StopLoss.Value:F5} tp={intent.TakeProfit?.Value.ToString("F5") ?? "none"} reason={intent.Reason}",
                            _clock.UtcNow));

                        if (_currentEquity.Balance == 0)
                        {
                            _logger.LogWarning("DISPATCH_SKIP|{Strategy}|{Symbol}|reason=equity not initialized",
                                strategy.Id, bar.Symbol.Value);
                            continue;
                        }
                        var equity = _currentEquity;
                        var orderCtx = await _orderDispatcher.DispatchAsync(intent, equity, bar.Close, _broker, [], ct);
                        if (orderCtx is null) continue;

                        var orderReq = new OrderRequest(intent, orderCtx.Lots, intent.Symbol,
                            intent.Direction, OrderType.Market, intent.LimitPrice);
                        _positionTracker.TrackOrder(orderCtx.OrderId, orderReq, orderCtx.RiskAmount);

                        _logger.LogInformation("ORDER|{Strategy}|{OrderId}|{Dir}|lots={Lots}|entry={Entry:F5}",
                            strategy.Id, orderCtx.OrderId, intent.Direction, orderCtx.Lots, bar.Close);

                        _progress?.Report(new BacktestProgressEvent(
                            _runContext.RunId, "ORDER",
                            $"ORDER {strategy.Id} {intent.Direction} lots={orderCtx.Lots:F2} entry≈{bar.Close:F5}",
                            _clock.UtcNow));
                    }

                    _journal?.Write("BAR_EVAL", bar.Symbol.Value, bar.OpenTimeUtc);

                    await DrainExecutionStreamAsync();

                    foreach (var (orderId, pos) in _positionTracker.OpenPositions.ToList())
                    {
                        if (pos.Symbol != bar.Symbol) continue;
                        bool exit = false;
                        if (pos.Direction == TradeDirection.Long)
                        {
                            if (bar.Low <= pos.CurrentStopLoss.Value) exit = true;
                            else if (pos.TakeProfit is not null && bar.High >= pos.TakeProfit.Value.Value) exit = true;
                        }
                        else
                        {
                            if (bar.High >= pos.CurrentStopLoss.Value) exit = true;
                            else if (pos.TakeProfit is not null && bar.Low <= pos.TakeProfit.Value.Value) exit = true;
                        }
                        if (exit)
                        {
                            _logger.LogInformation("BAR_EXIT|{Id}|{Symbol}|sl={SL:F5}|tp={TP}|low={Low:F5}|high={High:F5}",
                                orderId, pos.Symbol, pos.CurrentStopLoss.Value,
                                pos.TakeProfit?.Value ?? 0, bar.Low, bar.High);
                            await _broker.ClosePositionAsync(orderId, ct);
                        }
                    }

                    await DrainExecutionStreamAsync();

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

    private void HandleAccountUpdate(AccountUpdate update)
    {
        _riskManager.UpdateEquityLevels(update.Equity);
    }

    private void UpdateCrossRates(Bar bar)
    {
        if (bar.Symbol.Value == "GBPUSD")
            _crossRateStore.GbpUsdRate = bar.Close;
        else if (bar.Symbol.Value == "USDJPY")
            _crossRateStore.UsdJpyRate = bar.Close;
    }

    private decimal ResolveHalfSpread(Symbol symbol) => 0.0001m;

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

    private void BuildIndicatorSnapshot(Symbol symbol)
    {
        _indicatorValues.Clear();
    }

    private async Task RecomputeIndicatorsAsync(Symbol symbol, Timeframe tf, CancellationToken ct)
    {
        if (!_bars.TryGetValue(symbol, out var byTf)) return;
        if (!byTf.TryGetValue(tf, out var list)) return;
        IReadOnlyList<Bar> bars;
        lock (list) { bars = list.ToList(); }
        if (bars.Count < 2) return;
        try
        {
            var sma = _indicators.Sma(bars, Math.Min(20, bars.Count));
            _indicatorValues[$"{symbol}:{tf}:sma"] = sma;
            var atr = _indicators.Atr(bars, Math.Min(14, bars.Count));
            _indicatorValues[$"{symbol}:{tf}:atr"] = atr;
        }
        catch { }
    }

    private readonly EquitySnapshot _currentEquity = new(DateTime.MinValue, 0, 0, 0, 0, 0, 0, 0, EngineMode.Backtest);

    public long BarCount => Interlocked.Read(ref _barCount);
}

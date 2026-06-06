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
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly IRiskProfileResolver _riskProfileResolver;
    private readonly Func<string, string, decimal> _crossRateProvider;
    private readonly PersistenceService _persistence;
    private readonly ILogger<EngineWorker> _logger;

    private readonly ConcurrentDictionary<Symbol, ConcurrentDictionary<Timeframe, List<Bar>>> _bars = new();
    private readonly ConcurrentDictionary<string, double> _indicatorValues = new();
    private readonly Dictionary<Guid, OrderRequest> _pendingOrdersMap = new();
    private readonly Dictionary<Guid, Position> _openPositionsMap = new();
    private EquitySnapshot _currentEquity = new(DateTime.MinValue, 0, 0, 0, 0, 0, 0, 0, EngineMode.Backtest);

    private readonly Channel<ExecutionEvent> _executionEventChannel =
        Channel.CreateBounded<ExecutionEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true
        });

    private AccountUpdate? _latestAccountUpdate;
    private decimal _latestRiskAmount;
    private const int MaxBarHistory = 500;
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
        ISymbolInfoRegistry symbolRegistry,
        IRiskProfileResolver riskProfileResolver,
        Func<string, string, decimal> crossRateProvider,
        PersistenceService persistence,
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
        _symbolRegistry = symbolRegistry;
        _riskProfileResolver = riskProfileResolver;
        _crossRateProvider = crossRateProvider;
        _persistence = persistence;
        _dataFeed = dataFeed;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Engine starting. Mode={Mode} Strategies={Count}",
            EngineMode.Backtest, _strategies.Count());

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
                    HandleExecutionEvent(execEvent);

                if (_riskManager.ConsumeForceClosePending())
                {
                    _logger.LogCritical("Force-close triggered. Closing {Count} open positions", _openPositionsMap.Count);
                    foreach (var (_, pos) in _openPositionsMap.ToList())
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

                    var symbolPrefix = $"{tick.Symbol}:";
                    var indicators = _indicatorValues
                        .Where(kv => kv.Key.StartsWith(symbolPrefix))
                        .ToDictionary(kv => kv.Key[symbolPrefix.Length..], kv => kv.Value);
                    var context = new MarketContext(tick.Symbol, tick, barSnapshot, indicators, _clock.UtcNow);

                    var intent = strategy.Evaluate(context);
                    if (intent is null) continue;

                    var equity = Volatile.Read(ref _currentEquity);
                    var profile = _riskProfileResolver.Resolve(intent.RiskProfileId);
                    var violations = _riskManager.Validate(intent, equity, profile);

                    if (violations.Count > 0)
                    {
                        _logger.LogWarning("Trade blocked. Strategy={Strategy} Symbol={Symbol} Violations={V}",
                            strategy.Id, intent.Symbol, string.Join(", ", violations.Select(v => v.Code)));
                        continue;
                    }

                    var symbolInfo = _symbolRegistry.Get(intent.Symbol);
                    var entryPrice = intent.LimitPrice ?? new Price(tick.Mid);
                    var slPips = PipCalculator.Distance(entryPrice, intent.StopLoss, symbolInfo);
                    var pipValue = PipCalculator.PipValuePerLot(symbolInfo, entryPrice.Value, _crossRateProvider);

                    var lots = _riskManager.CalculateLotSize(intent, equity, profile);

                    var riskAmount = (decimal)slPips.Value * pipValue * lots;

                    _logger.LogInformation("Trade opened. Strategy={Strategy} Symbol={Symbol} Dir={Dir} Lots={Lots} Entry={Entry} SL={SL} TP={TP}",
                        strategy.Id, intent.Symbol, intent.Direction, lots, entryPrice.Value,
                        intent.StopLoss.Value, intent.TakeProfit?.Value);

                    var orderReq = new OrderRequest(intent, lots, intent.Symbol, intent.Direction, OrderType.Market, intent.LimitPrice);
                    var orderId = await _broker.SubmitOrderAsync(orderReq, ct);
                    _pendingOrdersMap[orderId] = orderReq;
                    _latestRiskAmount = riskAmount;
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

    private void HandleExecutionEvent(ExecutionEvent evt)
    {
        _logger.LogDebug("Execution event: OrderId={OrderId} State={State} Fill={Fill:F5} Lots={Lots}",
            evt.OrderId, evt.NewState, evt.FillPrice?.Value, evt.FilledLots);

        if (evt.NewState != OrderState.Filled || evt.FillPrice is null) return;
        var fillPrice = evt.FillPrice.Value.Value;

        if (!_pendingOrdersMap.TryGetValue(evt.OrderId, out var order))
        {
            if (_openPositionsMap.TryGetValue(evt.OrderId, out var pos))
            {
                var symbolInfo = _symbolRegistry.Get(pos.Symbol);
                var pnl = PipCalculator.GrossPnL(pos.Direction, pos.EntryPrice, new Price(fillPrice), pos.Lots, symbolInfo, _crossRateProvider);

                _openPositionsMap.Remove(evt.OrderId);
                _riskManager.DeregisterPosition(pos.Id);
                _positionManager.DeregisterPosition(pos.Id);

                foreach (var s in _strategies.Where(s => s.Id == pos.StrategyId))
                {
                    var tradeResult = new TradeResult(Guid.NewGuid(), pos.Id, pos.Symbol, pos.Direction, pos.Lots,
                        pos.EntryPrice, new Price(fillPrice), pos.CurrentStopLoss, pos.TakeProfit,
                        pos.OpenedAtUtc, _clock.UtcNow, pnl, Money.Zero(pnl.Currency), Money.Zero(pnl.Currency),
                        pnl, new Pips(0), 0, new Pips(0), new Pips(0), "TP", pos.StrategyId, "standard", EngineMode.Backtest);
                    s.OnTradeResult(tradeResult);
                    _ = _persistence.SaveTradeAsync(tradeResult, CancellationToken.None);
                }

                _logger.LogInformation("Trade closed. TradeId={Id} Exit={Exit:F5} PnL={PnL:F2}",
                    pos.Id, fillPrice, pnl.Amount);
            }
            return;
        }

        _pendingOrdersMap.Remove(evt.OrderId);

        var symbolInfo2 = _symbolRegistry.Get(order.Intent.Symbol);
        var position = new Position(
            Guid.NewGuid(), evt.OrderId, order.Intent.Symbol, order.Intent.Direction,
            evt.FilledLots, new Price(fillPrice), order.Intent.StopLoss, order.Intent.TakeProfit,
            _clock.UtcNow, order.Intent.StrategyId);

        _openPositionsMap[evt.OrderId] = position;
        _riskManager.RegisterPosition(position.Id, position.StrategyId, _latestRiskAmount);

        var posConfig = new PositionManagementConfig(
            position.StrategyId,
            new TrailingConfig(TrailingMethod.AtrMultiple, 0, 1.0, 1.0),
            true, 1.0, new Pips(1),
            new Money(_latestRiskAmount, "USD"));
        _positionManager.RegisterPosition(position, posConfig);

        _logger.LogInformation("Position opened. Id={Id} Symbol={Symbol} Dir={Dir} Lots={Lots} Entry={Entry:F5} SL={SL:F5}",
            position.Id, position.Symbol, position.Direction, position.Lots, position.EntryPrice.Value, position.CurrentStopLoss.Value);
    }

    private void HandleAccountUpdate(AccountUpdate update)
    {
        _riskManager.UpdateEquityLevels(update.Equity);

        var riskState = _riskManager.CurrentState;
        var equity = new EquitySnapshot(
            update.TimestampUtc, update.Balance, update.FloatingPnL, update.Equity,
            update.Equity, update.Equity, riskState.DailyDrawdownUsed, riskState.MaxDrawdownUsed, EngineMode.Backtest);
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
        var symbols = _strategies
            .SelectMany(s => Enumerable.Repeat(Symbol.Parse("EURUSD"), 1))
            .Distinct()
            .ToList();

        _logger.LogInformation("Warm-up: loading indicators for {Count} strategies, {SymbolCount} symbols",
            _strategies.Count(), symbols.Count);
        await Task.CompletedTask;
    }
}

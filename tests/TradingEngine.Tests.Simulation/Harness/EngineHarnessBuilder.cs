using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Application;
using TradingEngine.Host;
using TradingEngine.Risk;
using TradingEngine.Risk.Sizing;
using TradingEngine.Services;

namespace TradingEngine.Tests.Simulation.Harness;

public sealed class EngineHarnessBuilder
{
    private Symbol _symbol = Symbol.Parse("EURUSD");
    private decimal _initialBalance = 10_000m;
    private string _ruleSetId = "ftmo-standard";
    private string _runId = "harness-run";
    private IReadOnlyList<Bar> _bars = [];
    private readonly List<IStrategy> _strategies = [];
    private decimal _flattenAtFraction = 0.9m;
    private bool _enableBreachWatchdog = true;
    private readonly List<(Symbol Symbol, TradeDirection Direction, decimal EntryPrice, decimal Lots, decimal SlPrice, decimal? TpPrice)> _seedPositions = [];

    public EngineHarnessBuilder WithFlattenAtFraction(decimal fraction) { _flattenAtFraction = fraction; return this; }
    public EngineHarnessBuilder WithoutBreachWatchdog() { _enableBreachWatchdog = false; return this; }

    public EngineHarnessBuilder WithSymbol(Symbol symbol) { _symbol = symbol; return this; }
    public EngineHarnessBuilder WithInitialBalance(decimal balance) { _initialBalance = balance; return this; }
    public EngineHarnessBuilder WithRuleSet(string ruleSetId) { _ruleSetId = ruleSetId; return this; }
    public EngineHarnessBuilder WithRunId(string runId) { _runId = runId; return this; }
    public EngineHarnessBuilder WithBars(IReadOnlyList<Bar> bars) { _bars = bars; return this; }
    public EngineHarnessBuilder WithStrategy(IStrategy strategy) { _strategies.Add(strategy); return this; }

    public EngineHarnessBuilder WithSeedPosition(Symbol symbol, TradeDirection direction, decimal entryPrice, decimal lots, decimal slPrice, decimal? tpPrice)
    {
        _seedPositions.Add((symbol, direction, entryPrice, lots, slPrice, tpPrice));
        return this;
    }

    public Task<EngineHarness> BuildAsync()
    {
        IReadOnlyList<IStrategy> strategies = _strategies.Count > 0
            ? _strategies
            : [new AlwaysSignalStrategy()];
        var symbol = _symbol;
        var initialBalance = _initialBalance;

        var symbolInfo = new SymbolInfo(
            symbol, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);
        var symbolRegistry = Substitute.For<ISymbolInfoRegistry>();
        symbolRegistry.Get(symbol).Returns(symbolInfo);

        Func<string, string, decimal> crossRate = (_, _) => 1.0m;
        var clock = new ManualClock();
        clock.UtcNow = DateTime.UtcNow;

        var newsFilter = Substitute.For<INewsFilter>();
        var sessionFilter = new SessionFilter();
        var currencyExposure = Substitute.For<ICurrencyExposureTracker>();
        var sizingPolicy = new SizingPolicyOptions { FlattenAtFraction = (double)_flattenAtFraction };
        var riskManager = new RiskManager(
            symbolRegistry, crossRate, newsFilter, sessionFilter, clock,
            currencyExposure, governor: null, sizingPolicy);

        var ruleSet = MakeRuleSet(_ruleSetId);
        riskManager.SetActiveRuleSet(ruleSet);
        riskManager.InitializeDrawdown(initialBalance, "Fixed");

        var riskProfile = new RiskProfile(
            "standard", "Standard", 0.01, 0.05, 0.10, 100.0, 0.10, 0.5, 0.1, 5,
            false, _ruleSetId, LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);
        var constraints = ConstraintSet.Resolve(riskProfile, ruleSet);
        riskManager.SetConstraints(constraints);

        var passEstimator = Substitute.For<IPassProbabilityEstimator>();
        var complianceSvc = new PropFirmComplianceService(ruleSet, riskManager, clock, passEstimator);
        riskManager.SetComplianceService(complianceSvc);

        var sizePipeline = new SizeModifierPipeline(
            Enumerable.Empty<ISizeModifier>(), NullLogger<SizeModifierPipeline>.Instance);
        riskManager.SetSizePipeline(sizePipeline);

        var eventBus = new CollectingEventBus();
        var decisionJournal = new InMemoryDecisionJournal();

        var indicators = Substitute.For<IIndicatorService>();
        var positionManager = new PositionManager(symbolRegistry, indicators, NullLogger<PositionManager>.Instance);

        var riskProfileResolver = Substitute.For<IRiskProfileResolver>();
        riskProfileResolver.Resolve(Arg.Any<string>()).Returns(new RiskProfile(
            "standard", "Standard", 0.01, 0.05, 0.10, 100.0, 0.10, 0.5, 0.1, 5,
            false, _ruleSetId, LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3));

        var runContext = new EngineRunContext(_runId);

        var fakeVenue = new FakeVenue(symbolRegistry, crossRate);

        var effectExecutor = new EffectExecutor(
            fakeVenue, eventBus, decisionJournal,
            equitySink: null, progress: null,
            runContext, clock,
            NullLogger<EffectExecutor>.Instance,
            symbolRegistry, crossRate, strategies,
            riskManager, positionManager,
            governor: null, signalGate: null);

        var harnessEffectExecutor = new HarnessEffectExecutor(effectExecutor);

        var positionTracker = new PositionTracker(
            symbolRegistry, crossRate, riskManager, positionManager, eventBus, clock,
            NullLogger<PositionTracker>.Instance,
            harnessEffectExecutor);

        var crossRateStore = new CrossRateStore();

        var equity = new MutableBox<EquitySnapshot>(new EquitySnapshot(
            DateTime.MinValue, initialBalance, 0, initialBalance, initialBalance,
            initialBalance, 0, 0, EngineMode.Backtest));

        var accountProcessor = new AccountProcessor(
            riskManager, positionTracker, sizingPolicy,
            eventBus, clock, EngineMode.Backtest,
            crossRateStore, equitySink: null,
            setEquity: snapshot => equity.Value = snapshot,
            NullLogger.Instance,
            decisionJournal);

        foreach (var (seedSymbol, seedDir, seedEntry, seedLots, seedSl, seedTp) in _seedPositions)
        {
            positionTracker.SeedOpenPositions(
                new[]
                {
                    new OpenPositionInfo(Guid.NewGuid(), seedSymbol, seedDir, seedLots,
                        new Price(seedEntry), new Price(seedSl),
                        seedTp is { } tp ? new Price(tp) : null)
                }, strategies);
        }

        var dispatcher = new OrderDispatcher(
            riskManager, riskProfileResolver, symbolRegistry, crossRate,
            decisionJournal, runContext, NullLogger<OrderDispatcher>.Instance);

        var indicatorSnapshot = new IndicatorSnapshotService(indicators, strategies);

        var strategyBank = Substitute.For<IStrategyBank>();
        strategyBank.GetActive(Arg.Any<Symbol>(), Arg.Any<Timeframe>(), Arg.Any<MarketRegime>())
            .ReturnsForAnyArgs(strategies);

        var regimeDetector = Substitute.For<IRegimeDetector>();
        regimeDetector.Detect(Arg.Any<Symbol>(), Arg.Any<IReadOnlyList<Bar>>(), Arg.Any<Dictionary<string, double>>())
            .ReturnsForAnyArgs(MarketRegime.Trending);

        var tradingLoop = new TradingLoop(
            fakeVenue, indicatorSnapshot, dispatcher, positionTracker,
            strategyBank, regimeDetector, signalGate: null, governor: null, symbolRegistry,
            eventBus, clock, runContext,
            getCrossRate: crossRate,
            currentEquity: () => equity.Value,
            progress: null, journal: null,
            new TradingEngine.Services.EntryPlanner(symbolRegistry, NullLogger<TradingEngine.Services.EntryPlanner>.Instance),
            NullLogger.Instance);

        return Task.FromResult(new EngineHarness(
            tradingLoop, fakeVenue, positionTracker, riskManager, strategies,
            initialBalance, symbolRegistry, equity, _flattenAtFraction, _enableBreachWatchdog,
            eventBus, decisionJournal, accountProcessor));
    }

    private static PropFirmRuleSet MakeRuleSet(string id) => new(
        id, id, "Fixed", 0.05, 0.10, 0.10, 0,
        "BalancePlusFloating", "22:00:00", "UTC",
        false, "High", 0, 0,
        false, "21:00:00", "20:00:00", "NextTradingDay", false);
}

public sealed class HarnessEffectExecutor : IEffectExecutor
{
    private readonly EffectExecutor _inner;

    public HarnessEffectExecutor(EffectExecutor inner) => _inner = inner;

    public async Task ExecuteAsync(EngineEffect effect, CancellationToken ct)
    {
        switch (effect)
        {
            case SubmitOrder:
            case ModifyStopLoss:
            case ModifyTakeProfit:
                break;
            default:
                await _inner.ExecuteAsync(effect, ct);
                break;
        }
    }
}

public sealed class EngineHarness : IAsyncDisposable
{
    private TradingLoop Loop { get; }
    private IReadOnlyList<IStrategy> Strategies { get; }
    private decimal InitialBalance { get; }
    private ISymbolInfoRegistry SymbolRegistry { get; }
    private MutableBox<EquitySnapshot> EquityBox { get; }
    private decimal FlattenAtFraction { get; }
    private bool EnableBreachWatchdog { get; }
    private CollectingEventBus EventBus { get; }
    private AccountProcessor AccountProcessor { get; }

    public RiskManager Risk { get; }
    public FakeVenue Venue { get; }
    public PositionTracker Tracker { get; }
    public InMemoryDecisionJournal DecisionJournal { get; }
    public long BarCount => Loop.BarCount;

    public EngineHarness(
        TradingLoop loop, FakeVenue venue, PositionTracker positionTracker,
        RiskManager risk, IReadOnlyList<IStrategy> strategies,
        decimal initialBalance, ISymbolInfoRegistry symbolRegistry,
        MutableBox<EquitySnapshot> equityBox, decimal flattenAtFraction, bool enableBreachWatchdog,
        CollectingEventBus eventBus, InMemoryDecisionJournal decisionJournal,
        AccountProcessor accountProcessor)
    {
        Loop = loop;
        Venue = venue;
        Tracker = positionTracker;
        Strategies = strategies;
        InitialBalance = initialBalance;
        SymbolRegistry = symbolRegistry;
        EquityBox = equityBox;
        FlattenAtFraction = flattenAtFraction;
        EnableBreachWatchdog = enableBreachWatchdog;
        EventBus = eventBus;
        DecisionJournal = decisionJournal;
        AccountProcessor = accountProcessor;
        Risk = risk;
    }

    public async Task DriveBarsAsync(IReadOnlyList<Bar> bars, CancellationToken ct = default)
    {
        var equity = InitialBalance;
        var balance = InitialBalance;
        var closedPnlBaseline = 0m;

        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            Venue.BrokerTimeUtc = bar.OpenTimeUtc;
            Venue.CurrentMarketPrice = bar.Close;

            EquityBox.Value = new EquitySnapshot(
                bar.OpenTimeUtc, equity, 0, equity,
                Math.Max(equity, EquityBox.Value.PeakEquity),
                InitialBalance, 0, 0, EngineMode.Backtest);

            Risk.UpdateEquityLevels(equity);

            if (EnableBreachWatchdog)
            {
                await AccountProcessor.HandleAsync(new AccountUpdate(balance, equity, 0, bar.OpenTimeUtc));
            }

            if (Risk.CurrentState.InProtectionMode)
            {
                break;
            }

            await Loop.ProcessBarAsync(bar, ct);

            await DrainFillsAsync();

            await SimulateBarExitsAsync(bar, ct);

            await DrainFillsAsync();

            // Mirror EngineRunner: manage breakeven/trailing for still-open positions at the end of the bar.
            await Loop.UpdateTrailingStopsAsync(bar, ct);

            var totalClosedPnl = EventBus.OfType<TradeClosed>().Sum(tc => tc.Result.NetPnL.Amount);
            var delta = totalClosedPnl - closedPnlBaseline;
            closedPnlBaseline = totalClosedPnl;
            equity += delta;
        }

        Risk.UpdateEquityLevels(equity);
        if (EnableBreachWatchdog && !Risk.CurrentState.InProtectionMode)
        {
            await AccountProcessor.HandleAsync(new AccountUpdate(balance, equity, 0,
                bars.Count > 0 ? bars[^1].OpenTimeUtc : DateTime.UtcNow));
        }
    }

    private readonly List<ClosedTrade> _closedTrades = [];

    public IReadOnlyList<ClosedTrade> ClosedTrades => _closedTrades;

    private async Task DrainFillsAsync()
    {
        foreach (var evt in Venue.DrainExecutions())
        {
            var effects = await Tracker.OnExecutionAsync(evt, Strategies);
            if (effects is null) continue;
            foreach (var e in effects)
            {
                if (e is PublishTradeClosed tc)
                {
                    _closedTrades.Add(new ClosedTrade(
                        tc.Symbol, tc.Direction, tc.Lots, tc.EntryPrice.Value, tc.ExitPrice.Value, tc.ExitReason));
                }
            }
        }
    }

    private async Task SimulateBarExitsAsync(Bar bar, CancellationToken ct)
    {
        foreach (var (orderId, pos) in Tracker.OpenPositions.ToList())
        {
            if (pos.Symbol != bar.Symbol) continue;

            string? reason = null;
            Price? exitPrice = null;
            if (pos.Direction == TradeDirection.Long)
            {
                if (bar.Low <= pos.CurrentStopLoss.Value) { reason = "SL"; exitPrice = pos.CurrentStopLoss; }
                else if (pos.TakeProfit is not null && bar.High >= pos.TakeProfit.Value.Value) { reason = "TP"; exitPrice = pos.TakeProfit.Value; }
            }
            else
            {
                if (bar.High >= pos.CurrentStopLoss.Value) { reason = "SL"; exitPrice = pos.CurrentStopLoss; }
                else if (pos.TakeProfit is not null && bar.Low <= pos.TakeProfit.Value.Value) { reason = "TP"; exitPrice = pos.TakeProfit.Value; }
            }

            if (reason is not null && exitPrice is not null)
            {
                Tracker.SetCloseReason(orderId, reason);
                Venue.SetExitPrice(orderId, exitPrice.Value);
                await Venue.ClosePositionAsync(orderId, ct);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
    }
}

public sealed record ClosedTrade(
    Symbol Symbol, TradeDirection Direction, decimal Lots,
    decimal EntryPrice, decimal ExitPrice, string ExitReason);

public sealed class MutableBox<T>(T value)
{
    public T Value = value;
}

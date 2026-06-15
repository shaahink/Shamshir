using Microsoft.Extensions.Logging.Abstractions;
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

    public EngineHarnessBuilder WithSymbol(Symbol symbol) { _symbol = symbol; return this; }
    public EngineHarnessBuilder WithInitialBalance(decimal balance) { _initialBalance = balance; return this; }
    public EngineHarnessBuilder WithRuleSet(string ruleSetId) { _ruleSetId = ruleSetId; return this; }
    public EngineHarnessBuilder WithRunId(string runId) { _runId = runId; return this; }
    public EngineHarnessBuilder WithBars(IReadOnlyList<Bar> bars) { _bars = bars; return this; }
    public EngineHarnessBuilder WithStrategy(IStrategy strategy) { _strategies.Add(strategy); return this; }

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
        var clock = Substitute.For<IEngineClock>();
        clock.UtcNow.Returns(_ => DateTime.UtcNow);

        var newsFilter = Substitute.For<INewsFilter>();
        var sessionFilter = new SessionFilter();
        var currencyExposure = Substitute.For<ICurrencyExposureTracker>();
        var sizingPolicy = new SizingPolicyOptions();
        var riskManager = new RiskManager(
            symbolRegistry, crossRate, newsFilter, sessionFilter, clock,
            currencyExposure, governor: null, sizingPolicy);

        var ruleSet = MakeRuleSet(_ruleSetId);
        riskManager.SetActiveRuleSet(ruleSet);
        riskManager.InitializeDrawdown(initialBalance, "Fixed");

        var passEstimator = Substitute.For<IPassProbabilityEstimator>();
        var complianceSvc = new PropFirmComplianceService(ruleSet, riskManager, clock, passEstimator);
        riskManager.SetComplianceService(complianceSvc);

        var sizePipeline = new SizeModifierPipeline(
            Enumerable.Empty<ISizeModifier>(), NullLogger<SizeModifierPipeline>.Instance);
        riskManager.SetSizePipeline(sizePipeline);

        var eventBus = Substitute.For<IEventBus>();
        var decisionJournal = Substitute.For<IDecisionJournal>();
        var positionManager = Substitute.For<IPositionManager>();

        var riskProfileResolver = Substitute.For<IRiskProfileResolver>();
        riskProfileResolver.Resolve(Arg.Any<string>()).Returns(new RiskProfile(
            "standard", "Standard", 1.0, 5.0, 10.0, 100.0, 10.0, 0.5, 0.1, 5,
            false, _ruleSetId, LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3));

        var runContext = new EngineRunContext(_runId);

        var positionTracker = new PositionTracker(
            symbolRegistry, crossRate, riskManager, positionManager, eventBus, clock,
            NullLogger<PositionTracker>.Instance);

        var dispatcher = new OrderDispatcher(
            riskManager, riskProfileResolver, symbolRegistry, crossRate,
            decisionJournal, runContext, NullLogger<OrderDispatcher>.Instance);

        var indicators = Substitute.For<IIndicatorService>();
        var indicatorSnapshot = new IndicatorSnapshotService(indicators, strategies);

        var strategyBank = Substitute.For<IStrategyBank>();
        strategyBank.GetActive(Arg.Any<Symbol>(), Arg.Any<Timeframe>(), Arg.Any<MarketRegime>())
            .ReturnsForAnyArgs(strategies);

        var regimeDetector = Substitute.For<IRegimeDetector>();
        regimeDetector.Detect(Arg.Any<Symbol>(), Arg.Any<IReadOnlyList<Bar>>(), Arg.Any<Dictionary<string, double>>())
            .ReturnsForAnyArgs(MarketRegime.Trending);

        var fakeVenue = new FakeVenue();
        var equity = new MutableBox<EquitySnapshot>(new EquitySnapshot(
            DateTime.MinValue, initialBalance, 0, initialBalance, initialBalance,
            initialBalance, 0, 0, EngineMode.Backtest));

        var tradingLoop = new TradingLoop(
            fakeVenue, indicatorSnapshot, dispatcher, positionTracker,
            strategyBank, regimeDetector, signalGate: null, symbolRegistry,
            eventBus, clock, runContext,
            currentEquity: () => equity.Value,
            progress: null, journal: null, NullLogger.Instance);

        return Task.FromResult(new EngineHarness(
            tradingLoop, fakeVenue, positionTracker, riskManager, strategies,
            initialBalance, symbolRegistry, equity));
    }

    private static PropFirmRuleSet MakeRuleSet(string id) => new(
        id, id, "Fixed", 0.05, 0.10, 0.10, 0,
        "BalancePlusFloating", "22:00:00", "UTC",
        false, "High", 0, 0,
        false, "21:00:00", "20:00:00", "NextTradingDay", false);
}

public sealed class EngineHarness : IAsyncDisposable
{
    private TradingLoop Loop { get; }
    private IReadOnlyList<IStrategy> Strategies { get; }
    private decimal InitialBalance { get; }
    private ISymbolInfoRegistry SymbolRegistry { get; }
    private MutableBox<EquitySnapshot> EquityBox { get; }

    public RiskManager Risk { get; }
    public FakeVenue Venue { get; }
    public PositionTracker Tracker { get; }
    public long BarCount => Loop.BarCount;

    public EngineHarness(
        TradingLoop loop, FakeVenue venue, PositionTracker positionTracker,
        RiskManager risk, IReadOnlyList<IStrategy> strategies,
        decimal initialBalance, ISymbolInfoRegistry symbolRegistry,
        MutableBox<EquitySnapshot> equityBox)
    {
        Loop = loop;
        Venue = venue;
        Tracker = positionTracker;
        Strategies = strategies;
        InitialBalance = initialBalance;
        SymbolRegistry = symbolRegistry;
        EquityBox = equityBox;
        Risk = risk;
    }

    public async Task DriveBarsAsync(IReadOnlyList<Bar> bars, CancellationToken ct = default)
    {
        var equity = InitialBalance;

        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            Venue.BrokerTimeUtc = bar.OpenTimeUtc;

            EquityBox.Value = new EquitySnapshot(
                bar.OpenTimeUtc, equity, 0, equity,
                Math.Max(equity, EquityBox.Value.PeakEquity),
                InitialBalance, 0, 0, EngineMode.Backtest);

            Risk.UpdateEquityLevels(equity);

            await Loop.ProcessBarAsync(bar, ct);

            await DrainFillsAsync();

            await SimulateBarExitsAsync(bar, ct);

            await DrainFillsAsync();

            var closedPnL = ApproximateClosedPnL();
            equity += closedPnL;
        }

        Risk.UpdateEquityLevels(equity);
    }

    private async Task DrainFillsAsync()
    {
        foreach (var evt in Venue.DrainExecutions())
        {
            await Tracker.OnExecutionAsync(evt, Strategies);
        }
    }

    private async Task SimulateBarExitsAsync(Bar bar, CancellationToken ct)
    {
        foreach (var (orderId, pos) in Tracker.OpenPositions.ToList())
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
                await Venue.ClosePositionAsync(orderId, ct);
            }
        }
    }

    private int _lastCloseCount;

    private decimal ApproximateClosedPnL()
    {
        var totalCloses = Venue.CloseRequests.Count;
        var newCloses = totalCloses - _lastCloseCount;
        _lastCloseCount = totalCloses;

        if (newCloses <= 0) return 0;

        var symbolInfo = SymbolRegistry.Get(Symbol.Parse("EURUSD"));
        var pipValuePerLot = symbolInfo.PipSize * symbolInfo.ContractSize;
        return newCloses * -50m * pipValuePerLot * 0.01m;
    }

    public async ValueTask DisposeAsync()
    {
        await Task.CompletedTask;
    }
}

public sealed class MutableBox<T>(T value)
{
    public T Value = value;
}

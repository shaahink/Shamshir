using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Risk.Filters;
using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Strategies;

/// <summary>
/// P1.5.2 gate: aux-TF (e.g. H4) bars must become visible to indicator computation point-in-time — gated
/// by close time — not all at once at t=0. Before this fix, EngineRunner bulk-loaded the WHOLE run's aux
/// range and computed the aux indicator exactly once before the loop started, so every decision throughout
/// the run read the same run-end-inclusive value (lookahead bias). Drives the real BarEvaluator +
/// IndicatorSnapshotService pipeline with H4 bars registered via SetAuxBarSource, exactly as
/// EngineRunner.RunAsync now does, and asserts the EMA reading seen by an H1 decision bar reflects only
/// H4 bars that have actually closed by that point in sim-time.
/// </summary>
[Trait("Category", "KernelAcceptance")]
[Trait("Speed", "Fast")]
public sealed class AuxTfLookaheadTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");

    private static readonly SymbolInfo EurusdInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private static readonly RiskProfile StandardProfile = new(
        "standard", "Standard", 0.01, 0.05, 0.10, 100.0, 0.10, 0.5, 0.1, 5,
        false, "ftmo-standard", LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);

    private static PropFirmRuleSet FtmoRuleSet() => new(
        "ftmo-standard", "ftmo-standard", "Fixed", 0.05, 0.10, 0.10, 0,
        "BalancePlusFloating", "22:00:00", "UTC",
        false, "High", 0, 0,
        false, "21:00:00", "20:00:00", "NextTradingDay", false);

    /// <summary>Records the aux EMA value it sees on every bar; never fires a trade.</summary>
    private sealed class EmaRecorderStrategy : IStrategy
    {
        public readonly List<double> Observed = [];
        public string Id => "ema-recorder";
        public string DisplayName => "EMA Recorder (test only)";
        public Timeframe EntryTimeframe => Timeframe.H1;
        public IReadOnlyList<Timeframe> RequiredTimeframes => [Timeframe.H1, Timeframe.H4];
        public int RequiredBarCount => 1;
        public IReadOnlyList<IndicatorRequest> RequiredIndicators =>
            [new("EMA_TEST", IndicatorType.Ema, 3, Timeframe: Timeframe.H4)];
        public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
        public IStrategyConfig Config => new RecorderConfig();
        public StrategyStats Stats => new(0, 0, 0, 0);

        public TradeIntent? Evaluate(MarketContext context)
        {
            if (context.IndicatorValues.TryGetValue("EMA_TEST", out var ema))
                Observed.Add(ema);
            return null;
        }
        public void OnTradeResult(TradeResult result) { }
        public void Reset() => Observed.Clear();
    }

    private sealed record RecorderConfig : IStrategyConfig
    {
        public string Id => "ema-recorder";
        public string DisplayName => "EMA Recorder";
        public bool Enabled => true;
        public string RiskProfileId => "standard";
        public RegimeFilterOptions RegimeFilter => new();
        public OrderEntryOptions OrderEntry => new();
        public PositionManagementOptions PositionManagement => new();
        public ReentryOptions Reentry => new();
        public Timeframe EntryTimeframe { get; init; } = Timeframe.H1;
        public string? Symbol { get; init; }
        public IReadOnlyList<Timeframe> RequiredTimeframes { get; init; } = [];
    }

    private static BarEvaluator BuildEvaluator(IReadOnlyList<IStrategy> strategies, IIndicatorService realIndicators, IndicatorSnapshotService indicatorSnapshot)
    {
        var strategyBank = Substitute.For<IStrategyBank>();
        strategyBank.GetActive(Arg.Any<Symbol>(), Arg.Any<Timeframe>(), Arg.Any<MarketRegime>())
            .ReturnsForAnyArgs(strategies);

        var regimeDetector = Substitute.For<IRegimeDetector>();
        regimeDetector.Detect(Arg.Any<Symbol>(), Arg.Any<IReadOnlyList<Bar>>(), Arg.Any<IReadOnlyDictionary<string, double>>())
            .ReturnsForAnyArgs(MarketRegime.Trending);

        var symbolRegistry = Substitute.For<ISymbolInfoRegistry>();
        symbolRegistry.Get(Arg.Any<Symbol>()).Returns(EurusdInfo);

        var riskManager = Substitute.For<IRiskManager>();
        riskManager.ActiveRuleSet.Returns(FtmoRuleSet());
        riskManager.CheckComplianceBlock(Arg.Any<TradeIntent>(), Arg.Any<RiskProfile>()).Returns((string?)null);
        riskManager.Drawdown.Returns(TradingEngine.Engine.DrawdownReducer.CreateInitial(10_000m, "Fixed"));

        var riskProfileResolver = Substitute.For<IRiskProfileResolver>();
        riskProfileResolver.Resolve(Arg.Any<string>()).Returns(StandardProfile);

        return new BarEvaluator(
            indicatorSnapshot, strategyBank, regimeDetector, signalGate: null,
            new TradingEngine.Services.EntryPlanner(symbolRegistry, NullLogger<TradingEngine.Services.EntryPlanner>.Instance),
            symbolRegistry, getCrossRate: (_, _) => 1.0m,
            Substitute.For<INewsFilter>(), new SessionFilter(), riskManager, riskProfileResolver,
            governor: null, NullLogger.Instance, realIndicators);
    }

    [Fact]
    public async Task AuxEma_ReflectsOnlyBarsClosedAsOfDecisionTime_NotFullRunRange()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // H4: 5 bars flat at 1.0000, then 5 bars jumping to 1.1000 — EMA(3) over just the first half stays
        // near 1.0000; by the end of the full range it has risen sharply toward 1.1000.
        var h4Bars = new List<Bar>();
        var h4Time = start;
        for (var i = 0; i < 5; i++) { h4Bars.Add(new Bar(Eurusd, Timeframe.H4, h4Time, 1.0000m, 1.0000m, 1.0000m, 1.0000m, 100)); h4Time = h4Time.AddHours(4); }
        for (var i = 0; i < 5; i++) { h4Bars.Add(new Bar(Eurusd, Timeframe.H4, h4Time, 1.1000m, 1.1000m, 1.1000m, 1.1000m, 100)); h4Time = h4Time.AddHours(4); }

        // H1 decision bars spanning the same wall-clock range (40 hours) — one per hour.
        var h1Bars = new List<Bar>();
        var h1Time = start;
        for (var i = 0; i < 40; i++) { h1Bars.Add(new Bar(Eurusd, Timeframe.H1, h1Time, 1.05m, 1.05m, 1.05m, 1.05m, 100)); h1Time = h1Time.AddHours(1); }

        var strategy = new EmaRecorderStrategy();

        // Wire the aux source exactly as EngineRunner.RunAsync does — register, don't bulk-load.
        var realIndicators = new SkenderIndicatorService();
        var snapshot = new IndicatorSnapshotService(realIndicators, [strategy]);
        snapshot.SetAuxBarSource(Eurusd, Timeframe.H4, h4Bars);

        var directEvaluator = BuildEvaluator([strategy], realIndicators, snapshot);

        var state = new EngineState(
            new Dictionary<Guid, PositionState>(),
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            TradingEngine.Engine.DrawdownReducer.CreateInitial(10_000m, "Fixed"),
            0, ProtectionState.None, AccountView.Flat);

        foreach (var bar in h1Bars)
        {
            var barClosed = new BarClosed(bar.Symbol, bar.Timeframe, bar.Open, bar.High, bar.Low, bar.Close, bar.OpenTimeUtc);
            await directEvaluator.EvaluateAsync(barClosed, state, CancellationToken.None);
        }

        strategy.Observed.Should().NotBeEmpty();

        // Early decision bars (before ANY H4 bar has closed) see no EMA yet (0.0/default, filtered out by
        // TryGetValue never adding to Observed) — the first readings should reflect ONLY the flat 1.0000 H4
        // bars, not the later up-move.
        var earlyReadings = strategy.Observed.Take(3).ToList();
        earlyReadings.Should().OnlyContain(v => v < 1.02,
            "before the up-move H4 bars have closed, the aux EMA must reflect only the flat 1.0000 bars — " +
            "not leak the run's later 1.1000 bars");

        // Late decision bars (after all H4 bars, including the up-move, have closed) must see the risen EMA.
        var lateReadings = strategy.Observed.TakeLast(3).ToList();
        lateReadings.Should().OnlyContain(v => v > 1.05,
            "after the up-move H4 bars have closed, the aux EMA must reflect them");

        // The defining regression check: the EMA value actually CHANGES over the course of the run.
        // Pre-fix, RecomputeIndicatorsAsync for the aux TF ran once with the full range already loaded,
        // so every reading would be identical (the constant, run-end-inclusive value).
        strategy.Observed.Distinct().Count().Should().BeGreaterThan(1,
            "the aux EMA must vary as more H4 bars close over the run — a constant value across all " +
            "decision bars is exactly the lookahead-bias bug (P1.5.2)");
    }
}

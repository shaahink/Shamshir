using TradingEngine.Engine;
using TradingEngine.Host;

namespace TradingEngine.Tests.Simulation.Host;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class KernelWeekendFlattenEvaluatorTests
{
    private static readonly Symbol Eur = Symbol.Parse("EURUSD");

    private sealed record FakeWeekendConfig(bool ShouldFlatten) : IStrategyConfig
    {
        public string Id => "test";
        public string DisplayName => "Test";
        public bool Enabled => true;
        public string RiskProfileId => "standard";
        public Timeframe EntryTimeframe => Timeframe.H1;
        public IReadOnlyList<Timeframe> RequiredTimeframes => [Timeframe.H1];
        public int RequiredBarCount => 1;
        public OrderEntryOptions OrderEntry => new();
        public RegimeFilterOptions RegimeFilter => new();
        public PositionManagementOptions PositionManagement => new();
        public ReentryOptions Reentry => new();
        public string? Symbol => null;
        public string EntryRule => "";
        public string ExitFormula => "";
        public string Thesis => "";
        public int? ExpectedTradesPerWeek => null;
        public int? ExpectedHoldBars => null;
        public bool FlattenBeforeWeekend => ShouldFlatten;
    }

    private sealed class FakeStrategy(string id, IStrategyConfig config) : IStrategy
    {
        public string Id => id;
        public string DisplayName => id;
        public Timeframe EntryTimeframe => Timeframe.H1;
        public IReadOnlyList<Timeframe> RequiredTimeframes => [Timeframe.H1];
        public int RequiredBarCount => 1;
        public IReadOnlyList<IndicatorRequest> RequiredIndicators => [];
        public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
        public IStrategyConfig Config => config;
        public StrategyStats Stats => new(0, 0, 0, 0);
        public TradeIntent? Evaluate(MarketContext context) => null;
        public void OnTradeResult(TradeResult result) { }
        public void Reset() { }
    }

    private static PositionState OpenPosition(string strategyId, Guid? id = null) => new(
        id ?? Guid.NewGuid(), Guid.NewGuid(), Eur, TradeDirection.Long, 0.10m,
        new Price(1.1000m), new Price(1.0950m), new Price(1.1100m),
        new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), strategyId, PositionPhase.Open);

    private static EngineState StateWith(params PositionState[] positions)
    {
        var dict = positions.ToDictionary(p => p.PositionId);
        return new EngineState(dict,
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            DrawdownReducer.CreateInitial(10_000m),
            positions.Length, ProtectionState.None, AccountView.Flat);
    }

    [Fact]
    public void Evaluate_FridayBar_NextOpensSaturday_Flattens()
    {
        var strategy = new FakeStrategy("weekend-flat", new FakeWeekendConfig(ShouldFlatten: true));
        var evaluator = new KernelWeekendFlattenEvaluator([strategy]);
        var position = OpenPosition("weekend-flat");
        var state = StateWith(position);
        var fridayBar = new Bar(Eur, Timeframe.H1,
            new DateTime(2024, 1, 5, 23, 0, 0, DateTimeKind.Utc),
            1.1m, 1.1m, 1.1m, 1.1m, 100);
        var nextOpen = fridayBar.OpenTimeUtc + fridayBar.Timeframe.ToTimeSpan();
        nextOpen.DayOfWeek.Should().Be(DayOfWeek.Saturday, $"next bar opens at {nextOpen}");
        strategy.Config.FlattenBeforeWeekend.Should().BeTrue("config should opt in to weekend flatten");
        var result = evaluator.Evaluate(fridayBar, state);
        result.Should().ContainSingle("should flatten when next bar opens Saturday");
        result[0].PositionId.Should().Be(position.PositionId);
        result[0].Reason.Should().Be("WeekendFlatten");
    }

    [Fact]
    public void Evaluate_ThursdayBar_NextOpensFriday_DoesNotFlatten()
    {
        var strategy = new FakeStrategy("weekend-flat", new FakeWeekendConfig(ShouldFlatten: true));
        var evaluator = new KernelWeekendFlattenEvaluator([strategy]);
        var state = StateWith(OpenPosition("weekend-flat"));
        var thursdayBar = new Bar(Eur, Timeframe.H1,
            new DateTime(2024, 1, 4, 23, 0, 0, DateTimeKind.Utc), // Thursday 23:00 → opens Friday 00:00
            1.1m, 1.1m, 1.1m, 1.1m, 100);
        evaluator.Evaluate(thursdayBar, state).Should().BeEmpty("next bar opens Friday, not weekend");
    }

    [Fact]
    public void Evaluate_StrategyWithoutFlattenBeforeWeekend_DoesNotFlatten()
    {
        var strategy = new FakeStrategy("no-weekend", new FakeWeekendConfig(ShouldFlatten: false));
        var evaluator = new KernelWeekendFlattenEvaluator([strategy]);
        var state = StateWith(OpenPosition("no-weekend"));
        var fridayBar = new Bar(Eur, Timeframe.H1,
            new DateTime(2024, 1, 5, 23, 0, 0, DateTimeKind.Utc), // Friday 23:00 → opens Saturday
            1.1m, 1.1m, 1.1m, 1.1m, 100);
        evaluator.Evaluate(fridayBar, state).Should().BeEmpty("FlattenBeforeWeekend=false should skip");
    }

    [Fact]
    public void Evaluate_ClosedPosition_IsIgnored()
    {
        var strategy = new FakeStrategy("weekend-flat", new FakeWeekendConfig(ShouldFlatten: true));
        var evaluator = new KernelWeekendFlattenEvaluator([strategy]);
        var closed = OpenPosition("weekend-flat") with { Phase = PositionPhase.Closed };
        var state = StateWith(closed);
        var fridayBar = new Bar(Eur, Timeframe.H1,
            new DateTime(2024, 1, 5, 23, 0, 0, DateTimeKind.Utc), // Friday 23:00 → opens Saturday
            1.1m, 1.1m, 1.1m, 1.1m, 100);
        evaluator.Evaluate(fridayBar, state).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_SundayCheck_NextOpensSunday_AlsoFlattens()
    {
        var strategy = new FakeStrategy("weekend-flat", new FakeWeekendConfig(ShouldFlatten: true));
        var evaluator = new KernelWeekendFlattenEvaluator([strategy]);
        var position = OpenPosition("weekend-flat");
        var state = StateWith(position);
        var bar = new Bar(Eur, Timeframe.H1,
            new DateTime(2024, 1, 6, 23, 0, 0, DateTimeKind.Utc), // Saturday 23:00 → opens Sunday 00:00
            1.1m, 1.1m, 1.1m, 1.1m, 100);
        var result = evaluator.Evaluate(bar, state);
        result.Should().ContainSingle("next bar opening Sunday is also weekend-triggering");
        result[0].Reason.Should().Be("WeekendFlatten");
    }
}

using TradingEngine.Engine;
using TradingEngine.Host;
using TradingEngine.Strategies.SessionBreakout;

namespace TradingEngine.Tests.Simulation.Host;

/// <summary>
/// P2.4/D6 gate: time-flatten force-closes a strategy's open positions once the bar's time-of-day reaches
/// its configured FlattenAtUtc. SessionBreakoutConfig's FlattenTimeUtc (default 12:00) was previously dead
/// (PLAN.md: zero readers) — this is the loop-level consumer.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class KernelTimeFlattenEvaluatorTests
{
    private static readonly Symbol Eur = Symbol.Parse("EURUSD");

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
            DrawdownReducer.CreateInitial(10_000m, "Fixed"),
            positions.Length, ProtectionState.None, AccountView.Flat);
    }

    private static Bar BarAt(TimeSpan timeOfDay) =>
        new(Eur, Timeframe.H1, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) + timeOfDay, 1.1m, 1.1m, 1.1m, 1.1m, 100);

    [Fact]
    public void Evaluate_BarAtOrAfterFlattenTime_FlattensOpenPositionForThatStrategy()
    {
        var sessionBreakout = new FakeStrategy("session-breakout",
            new SessionBreakoutConfig("session-breakout", "Session Breakout", true, "standard", new()));
        var evaluator = new KernelTimeFlattenEvaluator([sessionBreakout]);

        var position = OpenPosition("session-breakout");
        var state = StateWith(position);

        var result = evaluator.Evaluate(BarAt(TimeSpan.FromHours(12)), state);

        result.Should().ContainSingle();
        result[0].PositionId.Should().Be(position.PositionId);
        result[0].Reason.Should().Be("TimeFlatten");
    }

    [Fact]
    public void Evaluate_BarBeforeFlattenTime_DoesNotFlatten()
    {
        var sessionBreakout = new FakeStrategy("session-breakout",
            new SessionBreakoutConfig("session-breakout", "Session Breakout", true, "standard", new()));
        var evaluator = new KernelTimeFlattenEvaluator([sessionBreakout]);

        var state = StateWith(OpenPosition("session-breakout"));

        evaluator.Evaluate(BarAt(TimeSpan.FromHours(11).Add(TimeSpan.FromMinutes(59))), state).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_StrategyWithNoFlattenConfigured_NeverFlattens()
    {
        // Any other strategy (no FlattenAtUtc override) must never be touched by this mechanism.
        var trendBreakout = new FakeStrategy("trend-breakout",
            new TradingEngine.Strategies.TrendBreakout.TrendBreakoutConfig());
        var evaluator = new KernelTimeFlattenEvaluator([trendBreakout]);

        var state = StateWith(OpenPosition("trend-breakout"));

        evaluator.Evaluate(BarAt(TimeSpan.FromHours(23)), state).Should().BeEmpty(
            "a strategy with no FlattenAtUtc configured must never be force-closed by time");
    }

    [Fact]
    public void Evaluate_ClosedPosition_IsIgnored()
    {
        var sessionBreakout = new FakeStrategy("session-breakout",
            new SessionBreakoutConfig("session-breakout", "Session Breakout", true, "standard", new()));
        var evaluator = new KernelTimeFlattenEvaluator([sessionBreakout]);

        var closed = OpenPosition("session-breakout") with { Phase = PositionPhase.Closed };
        var state = StateWith(closed);

        evaluator.Evaluate(BarAt(TimeSpan.FromHours(13)), state).Should().BeEmpty("already-closed positions must not be re-closed");
    }
}

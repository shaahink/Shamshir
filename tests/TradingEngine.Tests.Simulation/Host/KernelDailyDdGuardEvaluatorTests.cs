using TradingEngine.Engine;
using TradingEngine.Host;

namespace TradingEngine.Tests.Simulation.Host;

[Trait("Category", "Unit")]
[Trait("Speed", "Fast")]
public sealed class KernelDailyDdGuardEvaluatorTests
{
    private static readonly Symbol Eur = Symbol.Parse("EURUSD");

    private static PositionState OpenPosition(Guid? id = null) => new(
        id ?? Guid.NewGuid(), Guid.NewGuid(), Eur, TradeDirection.Long, 0.10m,
        new Price(1.1000m), new Price(1.0950m), new Price(1.1100m),
        new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), "trend-breakout", PositionPhase.Open);

    private static EngineState StateWith(decimal equity, DailyDdBase ddBase, params PositionState[] positions)
    {
        var dict = positions.ToDictionary(p => p.PositionId);
        var dd = ddBase == DailyDdBase.DailyStart
            ? DrawdownReducer.CreateInitial(10_000m) with { DailyStartEquity = 10_000m }
            : DrawdownReducer.CreateInitial(10_000m);
        return new EngineState(dict,
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            dd,
            positions.Length, ProtectionState.None, new AccountView(10_000m, equity, 0m));
    }

    private static Bar H1Bar() =>
        new(Eur, Timeframe.H1, new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc), 1.1m, 1.1m, 1.1m, 1.1m, 100);

    [Fact]
    public void Evaluate_EquityAboveFloor_ReturnsEmpty()
    {
        var evaluator = new KernelDailyDdGuardEvaluator(0.05m, DailyDdBase.InitialBalance);
        var position = OpenPosition();
        var state = StateWith(9_800m, DailyDdBase.InitialBalance, position);
        evaluator.Evaluate(H1Bar(), state).Should().BeEmpty("equity above the 5% floor must not trigger flatten");
    }

    [Fact]
    public void Evaluate_EquityBelowFloor_FlattensAllOpenPositions()
    {
        var evaluator = new KernelDailyDdGuardEvaluator(0.05m, DailyDdBase.InitialBalance);
        var p1 = OpenPosition();
        var p2 = OpenPosition();
        var state = StateWith(9_400m, DailyDdBase.InitialBalance, p1, p2);
        var result = evaluator.Evaluate(H1Bar(), state);
        result.Should().HaveCount(2, "all open positions must be flattened on DD breach");
        result[0].Reason.Should().Be("DailyDD");
        result[1].Reason.Should().Be("DailyDD");
    }

    [Fact]
    public void Evaluate_ZeroMaxLoss_ReturnsEmpty()
    {
        var evaluator = new KernelDailyDdGuardEvaluator(0m, DailyDdBase.InitialBalance);
        var state = StateWith(1m, DailyDdBase.InitialBalance, OpenPosition());
        evaluator.Evaluate(H1Bar(), state).Should().BeEmpty("disabled guard (fraction=0) must never flatten");
    }

    [Fact]
    public void Evaluate_NoOpenPositions_ReturnsEmpty()
    {
        var evaluator = new KernelDailyDdGuardEvaluator(0.05m, DailyDdBase.InitialBalance);
        var state = StateWith(9_400m, DailyDdBase.InitialBalance);
        evaluator.Evaluate(H1Bar(), state).Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_DailyStartBase_UsesDailyStartEquity()
    {
        var evaluator = new KernelDailyDdGuardEvaluator(0.05m, DailyDdBase.DailyStart);
        var position = OpenPosition();
        var state = StateWith(9_600m, DailyDdBase.DailyStart, position);
        evaluator.Evaluate(H1Bar(), state).Should().BeEmpty("equity 9600 > 9500 (= 10000 * 0.95), should not breach");
    }

    [Fact]
    public void Evaluate_DailyStartBase_BreachAtBelowDailyStartFloor()
    {
        var evaluator = new KernelDailyDdGuardEvaluator(0.05m, DailyDdBase.DailyStart);
        var position = OpenPosition();
        var state = StateWith(9_400m, DailyDdBase.DailyStart, position);
        evaluator.Evaluate(H1Bar(), state).Should().NotBeEmpty("equity 9400 < 9500 (= 10000 * 0.95), should breach");
    }
}

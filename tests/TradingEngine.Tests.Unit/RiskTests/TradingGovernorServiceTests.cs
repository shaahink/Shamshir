using TradingEngine.Engine;

namespace TradingEngine.Tests.Unit.RiskTests;

[Trait("Category", "Governor")]
public sealed class TradingGovernorServiceTests
{
    private static GovernorOptions StandardOptions() => new()
    {
        Enabled = true,
        ProfitLockEnabled = true,
        ProfitLockFraction = 0.6,
        LossBandFractions = [0.4, 0.6],
        LossBandMultipliers = [1.0, 0.5],
        StreakReduceAt = 3,
        StreakMultiplier = 0.5,
        StreakPauseAt = 5,
        CoolingOffBars = 24,
    };

    private static PropFirmRuleSet FtmoRules() => new(
        "ftmo-standard", "FTMO Standard", "Fixed", 0.05, 0.10, 0.10, 4,
        "BalancePlusFloatingMinusFeesAndSwaps", "22:00:00", "Europe/Prague",
        false, "High", 30, 15, false, "21:00:00", "20:00:00", "NextTradingDay", false);

    private static GovernorMachine MakeGovernor(GovernorOptions? options = null)
    {
        return new GovernorMachine(options ?? StandardOptions());
    }

    private static GovernorContext ContextWithDayPnl(decimal dayPnLFraction) =>
        new(dayPnLFraction, 100_000m, 100_000m + (100_000m * dayPnLFraction), 0, FtmoRules());

    [Fact]
    public void ProfitLock_BlocksTrades_OnSubsequentEvaluations()
    {
        var gov = MakeGovernor();
        var winningCtx = ContextWithDayPnl(+0.04m);

        var first = gov.Evaluate(winningCtx);
        first.AllowNewTrades.Should().BeFalse();
        first.State.Should().Be(GovernorTradingState.ProfitLocked);

        var second = gov.Evaluate(winningCtx);
        second.AllowNewTrades.Should().BeFalse();
        second.State.Should().Be(GovernorTradingState.ProfitLocked);
    }

    [Fact]
    public void ReducedBand_ReturnsCorrectSizeMultiplier()
    {
        var options = StandardOptions() with { LossBandMultipliers = [0.5, 0.0] };
        var gov = MakeGovernor(options);
        var lossCtx = ContextWithDayPnl(-0.024m);

        var decision = gov.Evaluate(lossCtx);
        decision.AllowNewTrades.Should().BeTrue();
        decision.State.Should().Be(GovernorTradingState.Reduced);
        decision.SizeMultiplier.Should().Be(0.5m);

        var snapshot = gov.GetSnapshot();
        snapshot.SizeMultiplier.Should().Be(0.5m);
    }

    [Fact]
    public void OnTradeClosed_WinResetsStreak_LossIncrements_BreakevenPreserves()
    {
        var gov = MakeGovernor();
        var ctx = ContextWithDayPnl(0m);

        gov.OnTradeClosed(LossTrade());
        gov.OnTradeClosed(LossTrade());
        gov.Evaluate(ctx);
        gov.GetSnapshot().ConsecutiveLosses.Should().Be(2);

        gov.OnTradeClosed(WinTrade());
        gov.Evaluate(ctx);
        gov.GetSnapshot().ConsecutiveLosses.Should().Be(0);

        gov.OnTradeClosed(LossTrade());
        gov.OnTradeClosed(LossTrade());
        gov.Evaluate(ctx);
        gov.GetSnapshot().ConsecutiveLosses.Should().Be(2);

        gov.OnTradeClosed(BreakevenTrade());
        gov.Evaluate(ctx);
        gov.GetSnapshot().ConsecutiveLosses.Should().Be(2);
    }

    [Fact]
    public void OnBar_DecrementsCoolingOff_EachCall()
    {
        var options = StandardOptions() with { StreakPauseAt = 1, CoolingOffBars = 5 };
        var gov = MakeGovernor(options);
        gov.OnTradeClosed(LossTrade());
        var ctx = ContextWithDayPnl(0m);
        var decision = gov.Evaluate(ctx);
        decision.State.Should().Be(GovernorTradingState.CoolingOff);
        decision.AllowNewTrades.Should().BeFalse();

        var t1 = new DateTime(2024, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        gov.OnBar(t1);
        gov.OnBar(t1);
        gov.OnBar(t1);
        gov.OnBar(t1);
        gov.OnBar(t1);

        // AF6: GovernorMachine.ApplyBar decrements every call (no timestamp guard).
        // After 5 bars, cooling-off expired.
        var snapshot = gov.GetSnapshot();
        snapshot.State.Should().Be(GovernorTradingState.Normal);
    }

    [Fact]
    public void Governor_BlocksTrading_AtSoftStop_Band()
    {
        var options = StandardOptions() with { LossBandMultipliers = [0.5, 0.0] };
        var gov = MakeGovernor(options);
        var lossCtx = ContextWithDayPnl(-0.03m);

        var decision = gov.Evaluate(lossCtx);
        decision.State.Should().Be(GovernorTradingState.SoftStop);
        decision.AllowNewTrades.Should().BeFalse();
        decision.SizeMultiplier.Should().Be(0m);
    }

    [Fact]
    public void Governor_EnablesTrading_WhenDisabled()
    {
        var options = StandardOptions() with { Enabled = false };
        var gov = MakeGovernor(options);
        var lossCtx = ContextWithDayPnl(-0.03m);

        var decision = gov.Evaluate(lossCtx);
        decision.AllowNewTrades.Should().BeTrue();
        decision.State.Should().Be(GovernorTradingState.Normal);
    }

    private static TradeResult LossTrade() => new(
        Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long, 0.1m,
        new Price(1.1000m), new Price(1.1000m), new Price(0m), null, DateTime.UtcNow, DateTime.UtcNow,
        new Money(-50m, "USD"), new Money(0m, "USD"), new Money(0m, "USD"),
        new Money(-50m, "USD"), new Pips(0), 0, new Pips(0), new Pips(0),
        "SL", "s1", "standard", EngineMode.Backtest);

    private static TradeResult WinTrade() => new(
        Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long, 0.1m,
        new Price(1.1000m), new Price(1.1000m), new Price(0m), null, DateTime.UtcNow, DateTime.UtcNow,
        new Money(+30m, "USD"), new Money(0m, "USD"), new Money(0m, "USD"),
        new Money(+30m, "USD"), new Pips(0), 0, new Pips(0), new Pips(0),
        "TP", "s1", "standard", EngineMode.Backtest);

    private static TradeResult BreakevenTrade() => new(
        Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long, 0.1m,
        new Price(1.1000m), new Price(1.1000m), new Price(0m), null, DateTime.UtcNow, DateTime.UtcNow,
        new Money(0m, "USD"), new Money(0m, "USD"), new Money(0m, "USD"),
        new Money(0m, "USD"), new Pips(0), 0, new Pips(0), new Pips(0),
        "FORCE", "s1", "standard", EngineMode.Backtest);
}

using Microsoft.Extensions.Logging;

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

    private static TradingGovernorService MakeGovernor(GovernorOptions? options = null)
    {
        return new TradingGovernorService(
            options ?? StandardOptions(),
            Substitute.For<ILogger<TradingGovernorService>>());
    }

    private static GovernorContext ContextWithDayPnl(decimal dayPnLFraction) =>
        new(dayPnLFraction, 100_000m, 100_000m + (100_000m * dayPnLFraction), 0, FtmoRules());

    [Fact]
    public void ProfitLock_BlocksTrades_OnSubsequentEvaluations()
    {
        var gov = MakeGovernor();
        var winningCtx = ContextWithDayPnl(+0.04m); // +4% day profit, 5% daily limit => gain >= 0.6×limit

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
        var lossCtx = ContextWithDayPnl(-0.024m); // -2.4% day => 48% of 5% limit => hits Reduced (0.4) band

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

        // Simulate 2 losses then a win
        gov.OnTradeClosed(LossTrade());
        gov.OnTradeClosed(LossTrade());
        gov.Evaluate(ctx);
        gov.GetSnapshot().ConsecutiveLosses.Should().Be(2);

        gov.OnTradeClosed(WinTrade());
        gov.Evaluate(ctx);
        gov.GetSnapshot().ConsecutiveLosses.Should().Be(0);

        // Breakeven preserves
        gov.OnTradeClosed(LossTrade());
        gov.OnTradeClosed(LossTrade());
        gov.Evaluate(ctx);
        gov.GetSnapshot().ConsecutiveLosses.Should().Be(2);

        gov.OnTradeClosed(BreakevenTrade());
        gov.Evaluate(ctx);
        gov.GetSnapshot().ConsecutiveLosses.Should().Be(2);
    }

    [Fact]
    public void OnBar_SameTimestampTwice_DecrementsOnce()
    {
        var options = StandardOptions() with { StreakPauseAt = 1, CoolingOffBars = 5 };
        var gov = MakeGovernor(options);
        // Trigger cooling-off with a loss
        gov.OnTradeClosed(LossTrade());
        var ctx = ContextWithDayPnl(0m);
        var decision = gov.Evaluate(ctx);
        decision.State.Should().Be(GovernorTradingState.CoolingOff);
        decision.AllowNewTrades.Should().BeFalse();

        var t1 = new DateTime(2024, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        // 5 calls with same timestamp => only 1 unique bar tick
        gov.OnBar(t1); gov.OnBar(t1); gov.OnBar(t1); gov.OnBar(t1); gov.OnBar(t1);

        // 4 bars must remain => still CoolingOff
        var snapshot = gov.GetSnapshot();
        snapshot.State.Should().Be(GovernorTradingState.CoolingOff);
    }

    [Fact]
    public void Governor_BlocksTrading_AtSoftStop_Band()
    {
        var options = StandardOptions() with { LossBandMultipliers = [0.5, 0.0] };
        var gov = MakeGovernor(options);
        // -3% day loss => 60% of 5% daily limit => hits SoftStop band (0.6)
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

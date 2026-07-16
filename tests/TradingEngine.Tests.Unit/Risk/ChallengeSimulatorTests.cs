namespace TradingEngine.Tests.Unit.Risk;

[Trait("Category", "Risk")]
public sealed class ChallengeSimulatorTests
{
    private static readonly PropFirmRuleSet FtmoStandard = new(
        "ftmo-standard", "FTMO Standard", "Fixed",
        0.05, 0.10, 0.10, 4,
        "BalancePlusFloatingMinusFeesAndSwaps", "22:00:00", "Europe/Prague",
        false, "High", 30, 15, false, "21:00:00", "20:00:00", "NextTradingDay", false);

    private static DailyEquityPoint Day(int dayOffset, decimal start, decimal end, int trades = 1) =>
        new(new DateTime(2026, 1, 1).AddDays(dayOffset), start, end, trades);

    [Fact]
    public void Passes_WhenTargetReachedAndMinTradingDaysMet()
    {
        var days = new[]
        {
            Day(0, 100_000, 102_000),
            Day(1, 102_000, 104_000),
            Day(2, 104_000, 108_000),
            Day(3, 108_000, 111_000), // +11% by day 4, 4 trading days
        };

        var result = ChallengeSimulator.SimulateWindow(days, FtmoStandard);

        result.Verdict.Should().Be(ChallengeVerdict.Pass);
        result.DayResolved.Should().Be(4);
        result.TradingDaysUsed.Should().Be(4);
    }

    [Fact]
    public void DoesNotPass_WhenTargetReachedButMinTradingDaysNotYetMet()
    {
        // +11% on day 1 alone, but only 1 trading day so far — must wait for day 4.
        var days = new[]
        {
            Day(0, 100_000, 111_000, trades: 1),
            Day(1, 111_000, 111_500, trades: 0), // no trade — doesn't count toward MinTradingDays
            Day(2, 111_500, 111_800, trades: 1),
            Day(3, 111_800, 112_000, trades: 1),
            Day(4, 112_000, 112_200, trades: 1), // 4th trading day, still above target
        };

        var result = ChallengeSimulator.SimulateWindow(days, FtmoStandard);

        result.Verdict.Should().Be(ChallengeVerdict.Pass);
        result.DayResolved.Should().Be(5);
        result.TradingDaysUsed.Should().Be(4);
    }

    [Fact]
    public void Fails_OnDailyLossBreach_BasedOnWindowStartBalance_NotDayStart()
    {
        // dailyDdBase=InitialBalance: the 5% cap is fixed to the WINDOW's starting equity,
        // not recomputed against a shrunken day-start equity.
        var days = new[]
        {
            Day(0, 100_000, 97_000),  // -3%, fine
            Day(1, 97_000, 91_900),   // day-over-day loss is 5,100 = 5.1% of the 100k window start -> breach
        };

        var result = ChallengeSimulator.SimulateWindow(days, FtmoStandard);

        result.Verdict.Should().Be(ChallengeVerdict.Fail);
        result.Reason.Should().Be("daily-loss-breach");
        result.DayResolved.Should().Be(2);
    }

    [Fact]
    public void Fails_OnMaxLossBreach()
    {
        var days = new[]
        {
            Day(0, 100_000, 98_000),
            Day(1, 98_000, 95_000),
            Day(2, 95_000, 89_000), // below the fixed 90,000 floor (10% max loss)
        };

        var result = ChallengeSimulator.SimulateWindow(days, FtmoStandard);

        result.Verdict.Should().Be(ChallengeVerdict.Fail);
        result.Reason.Should().Be("max-loss-breach");
    }

    [Fact]
    public void Incomplete_WhenWindowElapsesWithNoResolution()
    {
        var days = new[]
        {
            Day(0, 100_000, 100_500),
            Day(1, 100_500, 101_000),
            Day(2, 101_000, 100_800),
        };

        var result = ChallengeSimulator.SimulateWindow(days, FtmoStandard);

        result.Verdict.Should().Be(ChallengeVerdict.Incomplete);
        result.DayResolved.Should().BeNull();
        result.FinalReturnPercent.Should().BeApproximately(0.008, 0.0001);
    }

    [Fact]
    public void WorstDailyLoss_IsTrackedAcrossTheWindow_EvenWhenNotBreaching()
    {
        var days = new[]
        {
            Day(0, 100_000, 99_000),  // -1,000
            Day(1, 99_000, 96_500),   // -2,500, the worst
            Day(2, 96_500, 97_500),   // +1,000
        };

        var result = ChallengeSimulator.SimulateWindow(days, FtmoStandard);

        result.WorstDailyLossAmount.Should().Be(2_500m);
        result.WorstDailyLossPercent.Should().BeApproximately(0.025, 0.0001);
    }

    // iter-structural-edge S0 (sv2 pin): a day can end ABOVE the profit target and still have
    // lost more than the daily cap intraday-to-close (started the day far higher). The breach
    // check runs before the target check — daily-cap breach dominates target-hit.
    [Fact]
    public void DailyCapBreach_Dominates_TargetHit()
    {
        var days = new[]
        {
            Day(0, 100_000, 120_000), // +20% day 1; target reached but MinTradingDays (4) not met
            Day(1, 120_000, 112_000), // ends above the 110k target, but -8k > the 5k daily cap
        };

        var result = ChallengeSimulator.SimulateWindow(days, FtmoStandard);

        result.Verdict.Should().Be(ChallengeVerdict.Fail);
        result.Reason.Should().Be("daily-loss-breach");
    }

    [Fact]
    public void DailyStartBasis_RecomputesCapAgainstEachDaysOwnStartEquity()
    {
        var dailyStartRules = FtmoStandard with { DailyDdBase = DailyDdBase.DailyStart };
        var days = new[]
        {
            Day(0, 100_000, 96_000),  // -4% of day start (100k) — fine
            Day(1, 96_000, 91_100),   // -5.1% of THIS day's own start (96k) — breach
        };

        var result = ChallengeSimulator.SimulateWindow(days, dailyStartRules);

        result.Verdict.Should().Be(ChallengeVerdict.Fail);
        result.Reason.Should().Be("daily-loss-breach");
    }
}

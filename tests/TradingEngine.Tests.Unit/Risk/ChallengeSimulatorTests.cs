namespace TradingEngine.Tests.Unit.Risk;

[Trait("Category", "Risk")]
public sealed class ChallengeSimulatorTests
{
    private static readonly PropFirmRuleSet FtmoStandard = new(
        "ftmo-standard", "FTMO Standard", "Fixed",
        0.05, 0.10, 0.10, 4,
        "BalancePlusFloatingMinusFeesAndSwaps", "00:00:00", "Europe/Prague",
        true, "High", 2, 2, true, "21:00:00", "20:00:00", "NextTradingDay", false);

    // Trading days count OPENED trades (FTMO truth, V0) — the helper marks active days as both
    // opened and closed so the classic scenarios read unchanged.
    private static DailyEquityPoint Day(int dayOffset, decimal start, decimal end, int trades = 1) =>
        new(new DateTime(2026, 1, 1).AddDays(dayOffset), start, end, trades, trades);

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
        // dailyDdBase=InitialBalance: the cap AMOUNT is fixed at 5% of the window's initial
        // capital (never recomputed against a shrunken account); the day's floor hangs off the
        // previous day's close balance (V0 rule truth).
        var days = new[]
        {
            Day(0, 100_000, 97_000),  // -3%, fine
            Day(1, 97_000, 91_900),   // floor = 97,000 - 5,000 = 92,000; equity 91,900 breaches
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

    // V0 (FTMO rule truth, verified 2026-07-16): the daily-loss floor references the PREVIOUS
    // day's close BALANCE (stand-in for the midnight CE(S)T balance), not the day's start
    // equity. Realized gains banked yesterday RAISE today's floor even while equity lags.
    [Fact]
    public void DailyFloor_ReferencesPreviousDayCloseBalance_NotDayStartEquity()
    {
        var days = new[]
        {
            // Balance closed at 102k (banked wins) while equity carries a floating loss.
            new DailyEquityPoint(new DateTime(2026, 1, 1), 100_000m, 99_000m, 1, 1, EndBalance: 102_000m),
            // Floor today = 102k − 5k = 97k. Equity drop 99k→96.5k is only 2.5k (old
            // day-start-equity logic would NOT breach), but 96.5k ≤ 97k → breach.
            new DailyEquityPoint(new DateTime(2026, 1, 2), 99_000m, 96_500m, 1, 1),
        };

        var result = ChallengeSimulator.SimulateWindow(days, FtmoStandard);

        result.Verdict.Should().Be(ChallengeVerdict.Fail);
        result.Reason.Should().Be("daily-loss-breach");
        result.DayResolved.Should().Be(2);
    }

    // V0: a trading day is a day with a trade OPENED — days that only close a carried position
    // do not count toward MinTradingDays (a multi-day hold counts only its entry day).
    [Fact]
    public void TradingDays_CountOpenedDays_NotClosedDays()
    {
        DailyEquityPoint DayOc(int offset, decimal start, decimal end, int opened, int closed) =>
            new(new DateTime(2026, 1, 1).AddDays(offset), start, end, closed, opened);

        var closesOnly = new[]
        {
            DayOc(0, 100_000, 111_000, opened: 1, closed: 0), // target reached, 1 trading day
            DayOc(1, 111_000, 111_100, opened: 0, closed: 1),
            DayOc(2, 111_100, 111_200, opened: 0, closed: 1),
            DayOc(3, 111_200, 111_300, opened: 0, closed: 1),
        };
        ChallengeSimulator.SimulateWindow(closesOnly, FtmoStandard)
            .Verdict.Should().Be(ChallengeVerdict.Incomplete, "closing days do not count as trading days");

        var opensEachDay = new[]
        {
            DayOc(0, 100_000, 111_000, opened: 1, closed: 0),
            DayOc(1, 111_000, 111_100, opened: 1, closed: 0),
            DayOc(2, 111_100, 111_200, opened: 1, closed: 0),
            DayOc(3, 111_200, 111_300, opened: 1, closed: 0), // 4th opened day
        };
        var result = ChallengeSimulator.SimulateWindow(opensEachDay, FtmoStandard);
        result.Verdict.Should().Be(ChallengeVerdict.Pass);
        result.TradingDaysUsed.Should().Be(4);
    }

    // V0: breach checks look at the day's observed equity marks — a gap-through at the day's
    // OPEN (start equity below the floor) fails even if the close recovers.
    [Fact]
    public void MaxLossBreach_DetectedOnDayStartEquity_EvenWhenCloseRecovers()
    {
        var days = new[]
        {
            Day(0, 100_000, 98_000),
            Day(1, 89_000, 95_000), // opens below the 90k floor, closes back above
        };

        var result = ChallengeSimulator.SimulateWindow(days, FtmoStandard);

        result.Verdict.Should().Be(ChallengeVerdict.Fail);
        result.Reason.Should().Be("max-loss-breach");
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

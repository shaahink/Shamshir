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

    // ---- FTMO Challenge: 1-Step semantics (iter-pass-economics E1, live-verified 2026-07-27) ----

    private static readonly PropFirmRuleSet Ftmo1Step = new(
        "ftmo-1step", "FTMO 1-Step", ChallengeSimulator.TrailingEodDrawdownType,
        0.03, 0.10, 0.10, 0,
        "BalancePlusFloatingMinusFeesAndSwaps", "00:00:00", "Europe/Prague",
        true, "High", 0, 0, true, "21:00:00", "20:00:00", "NextTradingDay", false)
    { BestDayMaxShare = 0.5 };

    // The ML floor trails the highest EOD balance (recomputed after each close): banked gains
    // RAISE the floor. Under the static rule this drawdown would be legal; under EOD-trailing
    // it busts.
    [Fact]
    public void TrailingEodMaxLoss_FloorRisesWithBankedEodBalance()
    {
        var days = new[]
        {
            Day(0, 100_000, 105_000), // EOD balance 105k -> tomorrow's floor = 105k - 10k = 95k
            Day(1, 105_000, 94_900),  // above the static 90k floor, below the trailed 95k floor
        };

        ChallengeSimulator.SimulateWindow(days, Ftmo1Step with { BestDayMaxShare = null })
            .Verdict.Should().Be(ChallengeVerdict.Fail, "the trailed floor is 95k");
        ChallengeSimulator.SimulateWindow(days, Ftmo1Step with { BestDayMaxShare = null, DrawdownType = "Fixed" })
            .Reason.Should().NotBe("max-loss-breach", "the static floor is still 90k");
    }

    [Fact]
    public void TrailingEodMaxLoss_GrindDownToTrailedFloor_BustsWhereStaticWouldNot()
    {
        // Floor trails the day-0 high close (109k -> 99k) while each later day stays inside
        // the 3% MDL; the slow grind to 99k busts the trailing account. The same path under
        // the static rule (floor 90k) never resolves at all.
        var days = new[]
        {
            Day(0, 100_000, 109_000), // hwm 109k -> floor 99k from day 1 on
            Day(1, 109_000, 106_500), // -2.5k/day: never a 3k daily breach
            Day(2, 106_500, 104_000),
            Day(3, 104_000, 101_500),
            Day(4, 101_500, 99_000),  // 99k <= trailed floor 99k -> bust
        };

        var trailing = ChallengeSimulator.SimulateWindow(days, Ftmo1Step with { BestDayMaxShare = null });
        trailing.Verdict.Should().Be(ChallengeVerdict.Fail);
        trailing.Reason.Should().Be("max-loss-breach");
        trailing.DayResolved.Should().Be(5);

        ChallengeSimulator.SimulateWindow(days, Ftmo1Step with { BestDayMaxShare = null, DrawdownType = "Fixed" })
            .Verdict.Should().Be(ChallengeVerdict.Incomplete, "the static floor is 90k and nothing else fires");
    }

    // Best Day rule: a single day > 50% of Positive Days' Profit is NOT a breach — it defers
    // the pass until diluted (FTMO: "not considered a rule breach ... continue trading").
    [Fact]
    public void BestDayRule_DefersPass_UntilDiluted_ThenPasses()
    {
        var days = new[]
        {
            Day(0, 100_000, 108_000), // +8k best day
            Day(1, 108_000, 111_000), // +3k; target reached but best day 8k > 50% of 11k -> deferred
            Day(2, 111_000, 116_000), // +5k; positives 16k, best 8k = exactly 50% -> pass (50/50 legal)
        };

        var result = ChallengeSimulator.SimulateWindow(days, Ftmo1Step);

        result.Verdict.Should().Be(ChallengeVerdict.Pass);
        result.DayResolved.Should().Be(3);
    }

    [Fact]
    public void BestDayRule_ExactFiftyFiftyTwoDayPass_IsLegal()
    {
        // FTMO's own exceptional case: exactly 50% of the target on each of two days.
        var days = new[]
        {
            Day(0, 100_000, 105_000),
            Day(1, 105_000, 110_000),
        };

        var result = ChallengeSimulator.SimulateWindow(days, Ftmo1Step);

        result.Verdict.Should().Be(ChallengeVerdict.Pass);
        result.DayResolved.Should().Be(2);
    }

    [Fact]
    public void BestDayRule_WindowEndsWhileDeferred_IsIncomplete()
    {
        var days = new[]
        {
            Day(0, 100_000, 111_000), // +11k in one day — target hit, best day 100% of positives
            Day(1, 111_000, 111_200), // +0.2k — still 11k/11.2k > 50%
        };

        var result = ChallengeSimulator.SimulateWindow(days, Ftmo1Step);

        result.Verdict.Should().Be(ChallengeVerdict.Incomplete);
    }

    // Rule sets without the new fields must behave byte-identically (R1-style parity): the
    // 1-step scenarios above, run under the unchanged ftmo-standard rules, keep their old verdicts.
    [Fact]
    public void LegacyRuleSets_AreUnaffectedByNewFields()
    {
        var days = new[]
        {
            Day(0, 100_000, 105_000),
            Day(1, 105_000, 94_900), // static floor 90k: no ML breach; daily: 105k-5k=100k floor -> 94.9k breaches daily
        };

        var result = ChallengeSimulator.SimulateWindow(days, FtmoStandard);

        result.Verdict.Should().Be(ChallengeVerdict.Fail);
        result.Reason.Should().Be("daily-loss-breach");
    }
}

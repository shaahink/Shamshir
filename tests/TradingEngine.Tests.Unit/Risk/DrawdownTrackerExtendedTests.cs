namespace TradingEngine.Tests.Unit.Risk;

[Trait("Category", "Risk")]
public sealed class DrawdownTrackerExtendedTests
{
    [Fact]
    public void WeeklyDrawdown_AfterReset_StartsFromCurrentEquity()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(95_000);
        t.CurrentWeeklyDrawdown.Should().BeApproximately(0.05m, 0.0001m);

        t.OnWeeklyReset(95_000);
        t.OnEquityUpdate(95_000);
        t.CurrentWeeklyDrawdown.Should().Be(0m);
    }

    [Fact]
    public void MonthlyDrawdown_AccumulatesAcrossWeekResets()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(97_000);
        t.CurrentMonthlyDrawdown.Should().BeApproximately(0.03m, 0.0001m);

        t.OnWeeklyReset(97_000);
        t.OnEquityUpdate(97_000);
        t.CurrentMonthlyDrawdown.Should().BeApproximately(0.03m, 0.0001m);
    }

    [Fact]
    public void DrawdownVelocity_IncreasesWhenDDGrowsEachDay()
    {
        var t = Make(100_000);

        // Simulate 5 daily resets with increasing max DD
        t.OnEquityUpdate(98_000); t.OnDailyReset(98_000);
        t.OnEquityUpdate(96_000); t.OnDailyReset(96_000);
        t.OnEquityUpdate(94_000); t.OnDailyReset(94_000);
        t.OnEquityUpdate(92_000); t.OnDailyReset(92_000);
        t.OnEquityUpdate(90_000); t.OnDailyReset(90_000);

        t.DrawdownVelocity.Should().BeGreaterThan(0);
    }

    [Fact]
    public void IsAccelerating_FalseWhenDDStable()
    {
        var t = Make(100_000);

        for (int i = 0; i < 5; i++)
        {
            t.OnEquityUpdate(97_000);
            t.OnDailyReset(97_000);
        }

        t.IsAccelerating.Should().BeFalse();
    }

    [Fact]
    public void WeeklyDD_LimitExceeded_WithMultipleLossDays()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(96_000);
        t.CurrentWeeklyDrawdown.Should().BeApproximately(0.04m, 0.0001m);
    }

    [Fact]
    public void MonthlyDD_Resets_AfterMonthlyReset()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(92_000);
        t.CurrentMonthlyDrawdown.Should().BeApproximately(0.08m, 0.0001m);

        t.OnMonthlyReset(92_000);
        t.OnEquityUpdate(92_000);
        t.CurrentMonthlyDrawdown.Should().Be(0m);
    }

    private static DrawdownTracker Make(decimal initial, string type = "Fixed")
    {
        var t = new DrawdownTracker();
        t.Initialize(initial, type);
        return t;
    }
}

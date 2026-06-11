namespace TradingEngine.Tests.Simulation.Risk;

[Trait("Category", "Simulation")]
public sealed class WeeklyDDProtectionTests
{
    [Fact]
    public void WeeklyDDBreach_Detected_ByDrawdownTracker()
    {
        var tracker = new DrawdownTracker();
        tracker.Initialize(100_000);
        tracker.OnEquityUpdate(95_000);
        tracker.CurrentWeeklyDrawdown.Should().BeApproximately(0.05m, 0.0001m);
    }

    [Fact]
    public void MonthlyDD_Persists_AcrossWeeklyResets()
    {
        var tracker = new DrawdownTracker();
        tracker.Initialize(100_000);
        tracker.OnEquityUpdate(97_000);
        tracker.CurrentMonthlyDrawdown.Should().BeApproximately(0.03m, 0.0001m);

        tracker.OnWeeklyReset(97_000);
        tracker.OnEquityUpdate(97_000);
        tracker.CurrentMonthlyDrawdown.Should().BeApproximately(0.03m, 0.0001m);
    }
}

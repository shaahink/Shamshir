namespace TradingEngine.Tests.Unit.RiskTests;

[Trait("Category", "Risk")]
public sealed class DrawdownTrackerTests
{
    [Fact]
    public void Initialize_InitialBalance_NeverUpdated()
    {
        var tracker = new DrawdownTracker();
        tracker.Initialize(100_000);
        tracker.InitialAccountBalance.Should().Be(100_000);

        tracker.OnEquityUpdate(95_000);
        tracker.InitialAccountBalance.Should().Be(100_000);
    }

    [Fact]
    public void TrailingDD_TracksPeakEquity()
    {
        var tracker = new DrawdownTracker();
        tracker.Initialize(100_000, "Trailing");

        tracker.OnEquityUpdate(110_000);
        tracker.PeakEquity.Should().Be(110_000);

        tracker.OnEquityUpdate(105_000);
        tracker.CurrentMaxDrawdown.Should().BeApproximately(0.0455m, 0.001m);
    }

    [Fact]
    public void FixedDD_FloorIsConstant()
    {
        var tracker = new DrawdownTracker();
        tracker.Initialize(100_000, "Fixed");

        tracker.OnEquityUpdate(110_000);
        tracker.PeakEquity.Should().Be(110_000);

        var floor = tracker.GetMaxDrawdownFloor(0.10m);
    }

    [Fact]
    public void DailyReset_ClearsDailyDD()
    {
        var tracker = new DrawdownTracker();
        tracker.Initialize(100_000);

        tracker.OnEquityUpdate(97_000);
        tracker.CurrentDailyDrawdown.Should().BeGreaterThan(0);

        tracker.OnDailyReset(97_000);
        tracker.CurrentDailyDrawdown.Should().Be(0);
    }

    [Fact]
    public void DailyReset_DoesNotClearMaxDD()
    {
        var tracker = new DrawdownTracker();
        tracker.Initialize(100_000);

        tracker.OnEquityUpdate(90_000);
        var maxDdBeforeReset = tracker.CurrentMaxDrawdown;

        tracker.OnDailyReset(90_000);
        tracker.CurrentMaxDrawdown.Should().Be(maxDdBeforeReset);
    }

}

namespace TradingEngine.Tests.Unit.RiskTests;

[Trait("Category", "Risk")]
public sealed class DrawdownTrackerTests
{
    [Fact]
    public void Initialize_SetsInitialBalance_Immutable()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(90_000);
        t.InitialAccountBalance.Should().Be(100_000m);
    }

    [Fact]
    public void Initialize_Idempotent_SecondCallIgnored()
    {
        var t = Make(100_000);
        t.Initialize(200_000);
        t.InitialAccountBalance.Should().Be(100_000m);
    }

    [Fact]
    public void DailyDD_UsesInitialBalance_NotDailyStart()
    {
        var t = Make(100_000);
        t.OnDailyReset(104_000);
        t.OnEquityUpdate(100_000);

        t.CurrentDailyDrawdown.Should().Be(0m);
    }

    [Fact]
    public void DailyDD_AtExactFTMOLimit()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(95_000);
        t.CurrentDailyDrawdown.Should().BeApproximately(0.05m, 0.0001m);
    }

    [Fact]
    public void DailyDD_JustBelowFTMOLimit_NotBreached()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(95_001);
        t.CurrentDailyDrawdown.Should().BeLessThan(0.05m);
    }

    [Fact]
    public void DailyDD_JustAboveFTMOLimit_Breached()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(94_999);
        t.CurrentDailyDrawdown.Should().BeGreaterThan(0.05m);
    }

    [Fact]
    public void DailyDD_WinDoesNotGoNegative()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(110_000);
        t.CurrentDailyDrawdown.Should().Be(0m);
    }

    [Fact]
    public void DailyDD_AfterProfitableDayReset_PreviousLossStillCounts()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(97_000);
        t.OnDailyReset(97_000);
        t.CurrentDailyDrawdown.Should().BeApproximately(0.03m, 0.0001m);
    }

    [Fact]
    public void MaxDD_Fixed_UsesInitialBalance()
    {
        var t = Make(100_000, "Fixed");
        t.OnEquityUpdate(110_000);
        t.OnEquityUpdate(92_000);
        t.CurrentMaxDrawdown.Should().BeApproximately(0.08m, 0.0001m);
    }

    [Fact]
    public void MaxDD_Trailing_UsesPeakEquity()
    {
        var t = Make(100_000, "Trailing");
        t.OnEquityUpdate(110_000);
        t.OnEquityUpdate(105_000);
        t.CurrentMaxDrawdown.Should().BeApproximately(0.0455m, 0.001m);
    }

    [Fact]
    public void MaxDD_Trailing_PeakOnlyMovesUp()
    {
        var t = Make(100_000, "Trailing");
        t.OnEquityUpdate(110_000);
        t.OnEquityUpdate(105_000);
        t.PeakEquity.Should().Be(110_000m);

        t.OnEquityUpdate(112_000);
        t.PeakEquity.Should().Be(112_000m);
    }

    [Fact]
    public void MaxDD_DoesNotClearOnDailyReset()
    {
        var t = Make(100_000);
        t.OnEquityUpdate(90_000);
        var maxDdBefore = t.CurrentMaxDrawdown;
        t.OnDailyReset(90_000);
        t.CurrentMaxDrawdown.Should().Be(maxDdBefore);
    }

    [Fact]
    public void GetDailyLossLimit_ReturnsCorrectFloor()
    {
        var t = Make(100_000);
        t.GetDailyLossLimit(0.05m).Should().Be(95_000m);
    }

    [Fact]
    public void GetMaxDrawdownFloor_Fixed_UsesInitial()
    {
        var t = Make(100_000, "Fixed");
        t.OnEquityUpdate(110_000);
        t.GetMaxDrawdownFloor(0.10m).Should().Be(90_000m);
    }

    [Fact]
    public void GetMaxDrawdownFloor_Trailing_UsesPeak()
    {
        var t = Make(100_000, "Trailing");
        t.OnEquityUpdate(110_000);
        t.GetMaxDrawdownFloor(0.10m).Should().Be(99_000m);
    }

    private static DrawdownTracker Make(decimal initial, string type = "Fixed")
    {
        var t = new DrawdownTracker();
        t.Initialize(initial, type);
        return t;
    }
}

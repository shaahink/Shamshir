namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Risk")]
public sealed class DrawdownScalerPhase3BTests
{
    [Fact]
    public void ComputeScaleFactor_BelowThreshold_ReturnsOne()
    {
        var factor = DrawdownScaler.ComputeScaleFactor(0.02m, 0.10m, 0.5, 0.5);
        factor.Should().Be(1.0);
    }

    [Fact]
    public void ComputeScaleFactor_AtThreshold_ReturnsOne()
    {
        var factor = DrawdownScaler.ComputeScaleFactor(0.05m, 0.10m, 0.5, 0.5);
        factor.Should().Be(1.0);
    }

    [Fact]
    public void ComputeScaleFactor_AboveThreshold_Reduces()
    {
        var factor = DrawdownScaler.ComputeScaleFactor(0.08m, 0.10m, 0.5, 0.5);
        factor.Should().BeLessThan(1.0);
        factor.Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void ComputeScaleFactor_AtLimit_ReturnsFloor()
    {
        var factor = DrawdownScaler.ComputeScaleFactor(0.10m, 0.10m, 0.5, 0.5);
        factor.Should().Be(0.5);
    }
}

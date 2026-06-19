using TradingEngine.Engine;

namespace TradingEngine.Tests.Unit.RiskTests;

[Trait("Category", "Risk")]
public sealed class PositionSizerTests
{
    [Fact]
    public void Calculate_StandardInput_ReturnsCorrectLots()
    {
        var riskAmount = 10_000m * 0.01m;
        var lots = KernelSizing.FromRiskAmount(
            riskAmount, slPips: 21m, pipValuePerLot: 10m,
            maxLots: 100m, minLots: 0.01m, lotStep: 0.01m);

        lots.Should().Be(0.47m);
    }

    [Fact]
    public void Calculate_ResultFlooredToLotStep_NeverRoundsUp()
    {
        var riskAmount = 10_000m * 0.01m;
        var lots = KernelSizing.FromRiskAmount(
            riskAmount, slPips: 43m, pipValuePerLot: 10m,
            maxLots: 100m, minLots: 0.01m, lotStep: 0.01m);

        lots.Should().Be(0.23m);
    }

    [Fact]
    public void Calculate_CappedAtMaxLots()
    {
        var riskAmount = 100_000m * 0.50m;
        var lots = KernelSizing.FromRiskAmount(
            riskAmount, slPips: 5m, pipValuePerLot: 10m,
            maxLots: 5.0m, minLots: 0.01m, lotStep: 0.01m);

        lots.Should().Be(5.0m);
    }

    [Fact]
    public void Calculate_AtMinLotsWhenEquityZero_ReturnsMinLots()
    {
        var lots = KernelSizing.FromRiskAmount(
            0m, slPips: 50m, pipValuePerLot: 10m,
            maxLots: 100m, minLots: 0.01m, lotStep: 0.01m);

        lots.Should().Be(0.01m);
    }

    [Fact]
    public void Calculate_WithDrawdownScale_ReducesLots()
    {
        var riskAmount = 100_000m * 0.01m;
        var fullLots = KernelSizing.FromRiskAmount(
            riskAmount, slPips: 50m, pipValuePerLot: 10m,
            maxLots: 100m, minLots: 0.01m, lotStep: 0.01m);

        var scaledLots = KernelSizing.FromRiskAmount(
            riskAmount * 0.5m, slPips: 50m, pipValuePerLot: 10m,
            maxLots: 100m, minLots: 0.01m, lotStep: 0.01m);

        scaledLots.Should().BeLessThan(fullLots);
        scaledLots.Should().BeApproximately(fullLots * 0.5m, 0.01m);
    }
}

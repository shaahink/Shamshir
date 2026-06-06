namespace TradingEngine.Tests.Unit.RiskTests;

[Trait("Category", "Risk")]
public sealed class PositionSizerTests
{
    [Fact]
    public void Calculate_StandardInput_ReturnsCorrectLots()
    {
        var lots = PositionSizer.Calculate(
            equity: 10_000m,
            riskPercent: RiskPercent.Parse(0.01),
            stopLossDistance: new Pips(21),
            pipValue: 10m,
            drawdownScaleFactor: 1.0m,
            maxLots: 100m,
            brokerMinLots: 0.01m,
            brokerLotStep: 0.01m);

        lots.Should().Be(0.47m);
    }

    [Fact]
    public void Calculate_ResultFlooredToLotStep_NeverRoundsUp()
    {
        var lots = PositionSizer.Calculate(
            equity: 10_000m, riskPercent: RiskPercent.Parse(0.01),
            stopLossDistance: new Pips(43), pipValue: 10m,
            drawdownScaleFactor: 1.0m, maxLots: 100m,
            brokerMinLots: 0.01m, brokerLotStep: 0.01m);

        lots.Should().Be(0.23m);
    }

    [Fact]
    public void Calculate_CappedAtMaxLots()
    {
        var lots = PositionSizer.Calculate(
            equity: 100_000m, riskPercent: RiskPercent.Parse(0.50),
            stopLossDistance: new Pips(5), pipValue: 10m,
            drawdownScaleFactor: 1.0m, maxLots: 5.0m,
            brokerMinLots: 0.01m, brokerLotStep: 0.01m);

        lots.Should().Be(5.0m);
    }

    [Fact]
    public void Calculate_AtMinLotsWhenEquityZero_ReturnsMinLots()
    {
        var lots = PositionSizer.Calculate(
            equity: 0m, riskPercent: RiskPercent.Parse(0.01),
            stopLossDistance: new Pips(50), pipValue: 10m,
            drawdownScaleFactor: 1.0m, maxLots: 100m,
            brokerMinLots: 0.01m, brokerLotStep: 0.01m);

        lots.Should().Be(0.01m);
    }

    [Fact]
    public void Calculate_WithDrawdownScale_ReducesLots()
    {
        var fullLots = PositionSizer.Calculate(
            100_000m, RiskPercent.Parse(0.01), new Pips(50), 10m,
            drawdownScaleFactor: 1.0m, 100m, 0.01m, 0.01m);

        var scaledLots = PositionSizer.Calculate(
            100_000m, RiskPercent.Parse(0.01), new Pips(50), 10m,
            drawdownScaleFactor: 0.5m, 100m, 0.01m, 0.01m);

        scaledLots.Should().BeLessThan(fullLots);
        scaledLots.Should().BeApproximately(fullLots * 0.5m, 0.01m);
    }
}

namespace TradingEngine.Tests.Unit.RiskTests;

[Trait("Category", "Risk")]
public sealed class PositionSizerTests
{
    [Fact]
    public void Calculate_NormalCase_ReturnsCorrectLots()
    {
        var result = PositionSizer.Calculate(
            equity: 10_000,
            riskPercent: RiskPercent.Parse(0.01),
            stopLossDistance: new Pips(21),
            pipValue: 10m,
            drawdownScaleFactor: 1.0m,
            maxLots: 100m,
            brokerMinLots: 0.01m,
            brokerLotStep: 0.01m);

        result.Should().Be(0.47m);
    }

    [Fact]
    public void Calculate_WithDDScaling_ReducesLots()
    {
        var result = PositionSizer.Calculate(
            equity: 10_000,
            riskPercent: RiskPercent.Parse(0.01),
            stopLossDistance: new Pips(21),
            pipValue: 10m,
            drawdownScaleFactor: 0.5m,
            maxLots: 100m,
            brokerMinLots: 0.01m,
            brokerLotStep: 0.01m);

        result.Should().Be(0.23m);
    }

    [Fact]
    public void Calculate_AlwaysRoundsDown()
    {
        var result = PositionSizer.Calculate(
            equity: 10_000,
            riskPercent: RiskPercent.Parse(0.01),
            stopLossDistance: new Pips(21),
            pipValue: 10m,
            drawdownScaleFactor: 1.0m,
            maxLots: 100m,
            brokerMinLots: 0.01m,
            brokerLotStep: 0.01m);

        result.Should().Be(0.47m);
        result.Should().NotBe(0.48m);
    }

    [Fact]
    public void Calculate_RespectsMinLots()
    {
        var result = PositionSizer.Calculate(
            equity: 100,
            riskPercent: RiskPercent.Parse(0.01),
            stopLossDistance: new Pips(50),
            pipValue: 10m,
            drawdownScaleFactor: 1.0m,
            maxLots: 100m,
            brokerMinLots: 0.01m,
            brokerLotStep: 0.01m);

        result.Should().Be(0.01m);
    }
}

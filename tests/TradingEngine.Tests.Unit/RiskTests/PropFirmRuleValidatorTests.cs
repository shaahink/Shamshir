namespace TradingEngine.Tests.Unit.RiskTests;

[Trait("Category", "Risk")]
public sealed class PropFirmRuleValidatorTests
{
    [Fact]
    public void IsProfitTargetMet_ChecksBalance_NotEquity()
    {
        var validator = new PropFirmRuleValidator();
        var result = validator.IsProfitTargetMet(
            currentBalance: 110_000,
            initialBalance: 100_000,
            profitTargetPercent: 0.10);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsProfitTargetMet_ReturnsFalse_WhenBelowTarget()
    {
        var validator = new PropFirmRuleValidator();
        var result = validator.IsProfitTargetMet(
            currentBalance: 105_000,
            initialBalance: 100_000,
            profitTargetPercent: 0.10);

        result.Should().BeFalse();
    }
}

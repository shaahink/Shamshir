namespace TradingEngine.Tests.Unit.RiskTests;

[Trait("Category", "Risk")]
public sealed class PropFirmRuleValidatorTests
{
    [Fact] // F3 / M6 — profit target keys off EQUITY, not balance
    public void IsProfitTargetMet_UsesEquity_NotBalance()
    {
        var validator = new PropFirmRuleValidator();
        var result = validator.IsProfitTargetMet(
            currentEquity: 110_000,
            initialBalance: 100_000,
            profitTargetPercent: 0.10);

        result.Should().BeTrue();
    }

    [Fact] // F3 / M6 — an open profitable position pushes equity over target while balance is still under
    public void Ftmo_ProfitTarget_MetByEquityNotBalance()
    {
        var validator = new PropFirmRuleValidator();

        // Realized balance 105k is BELOW the 110k target, but floating profit lifts equity to 112k → met.
        var byEquity = validator.IsProfitTargetMet(
            currentEquity: 112_000, initialBalance: 100_000, profitTargetPercent: 0.10);
        byEquity.Should().BeTrue("the target is met by equity (balance 105k would say 'not met')");

        // Sanity: had the check used the 105k balance, it would (wrongly) report not-met.
        var balanceWouldSayNo = validator.IsProfitTargetMet(
            currentEquity: 105_000, initialBalance: 100_000, profitTargetPercent: 0.10);
        balanceWouldSayNo.Should().BeFalse();
    }

    [Fact]
    public void IsProfitTargetMet_ReturnsFalse_WhenBelowTarget()
    {
        var validator = new PropFirmRuleValidator();
        var result = validator.IsProfitTargetMet(
            currentEquity: 105_000,
            initialBalance: 100_000,
            profitTargetPercent: 0.10);

        result.Should().BeFalse();
    }
}

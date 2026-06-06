namespace TradingEngine.Risk;

public sealed class PropFirmRuleValidator
{
    public IReadOnlyList<RiskViolation> Validate(
        TradeIntent intent,
        EquitySnapshot equity,
        PropFirmRuleSet ruleSet)
    {
        var violations = new List<RiskViolation>();

        if (equity.CurrentDailyDrawdown >= (decimal)ruleSet.MaxDailyLossPercent)
        {
            violations.Add(new("DAILY_DD_LIMIT",
                $"Daily drawdown {equity.CurrentDailyDrawdown:P1} exceeds limit {ruleSet.MaxDailyLossPercent:P1}"));
        }

        if (equity.CurrentMaxDrawdown >= (decimal)ruleSet.MaxTotalLossPercent)
        {
            violations.Add(new("MAX_DD_LIMIT",
                $"Max drawdown {equity.CurrentMaxDrawdown:P1} exceeds limit {ruleSet.MaxTotalLossPercent:P1}"));
        }

        return violations;
    }

    public bool IsProfitTargetMet(decimal currentBalance, decimal initialBalance, double profitTargetPercent)
    {
        var target = initialBalance * (1 + (decimal)profitTargetPercent);
        return currentBalance >= target;
    }
}

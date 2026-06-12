namespace TradingEngine.Domain;

public record SizingPolicyOptions
{
    public double FlattenAtFraction { get; init; } = 0.9;
    public double BudgetUseFraction { get; init; } = 0.25;
    public double MaxPortfolioHeatRiskMultiples { get; init; } = 3.0;
}

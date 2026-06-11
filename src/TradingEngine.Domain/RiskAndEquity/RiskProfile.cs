namespace TradingEngine.Domain;

public record RiskProfile(
    string Id,
    string DisplayName,
    double RiskPerTradePercent,
    double MaxDailyDrawdownPercent,
    double MaxTotalDrawdownPercent,
    double MaxSlPips,
    double MaxExposurePercent,
    double DrawdownScaleThreshold,
    double DrawdownScaleFloor,
    int MaxConcurrentPositions,
    bool AllowHedging,
    string PropFirmRuleSetId,
    LotSizingMethod LotSizingMethod = LotSizingMethod.PercentRisk,
    decimal FixedLots = 0.1m,
    decimal FixedDollarRisk = 0m,
    double KellyFraction = 0.25,
    double AntiMartingaleMultiplier = 1.5,
    int AntiMartingaleMaxSteps = 3)
{
    public double MaxExposurePerCurrencyPercent { get; init; } = 0.05;
    public SizeModifierOptions SizeModifiers { get; init; } = new();
}


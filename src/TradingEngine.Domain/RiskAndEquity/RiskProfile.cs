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
    string PropFirmRuleSetId);

namespace TradingEngine.Domain;

public enum GovernorTradingState
{
    Normal,
    Reduced,
    SoftStop,
    CoolingOff,
    ProfitLocked,
    HardStop
}

public record GovernorDecision(
    bool AllowNewTrades,
    decimal SizeMultiplier,
    GovernorTradingState State,
    string Reason);

public record GovernorContext(
    decimal DayRealizedPnLPercent,
    decimal DayStartEquity,
    decimal CurrentEquity,
    int ConsecutiveLosses,
    PropFirmRuleSet Rules);

public record GovernorSnapshot(
    GovernorTradingState State,
    decimal SizeMultiplier,
    int ConsecutiveLosses,
    decimal DayRealizedPnLPercent,
    decimal DistanceToDailyLimitFraction,
    string Reason);

namespace TradingEngine.Domain;

public sealed record GovernorState(
    GovernorTradingState State,
    int ConsecutiveLosses,
    int CoolingOffBarsRemaining,
    decimal DayNetPnLFraction,
    decimal LastSizeMultiplier,
    bool ProfitLockedToday,
    string Reason);

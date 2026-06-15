namespace TradingEngine.Domain;

public sealed record DrawdownState(
    decimal InitialAccountBalance,
    decimal PeakEquity,
    decimal DailyStartEquity,
    decimal WeeklyStartEquity,
    decimal MonthlyStartEquity,
    decimal CurrentDailyDrawdown,
    decimal CurrentMaxDrawdown,
    decimal CurrentWeeklyDrawdown,
    decimal CurrentMonthlyDrawdown,
    decimal DrawdownVelocity,
    string DrawdownType,
    string DailyDdBaseMode = "InitialBalance",
    bool IsInitialized = false,
    IReadOnlyList<decimal> VelocityWindow = null!)
{
    public IReadOnlyList<decimal> VelocityWindow { get; init; } = VelocityWindow ?? [];

    public bool IsAccelerating => DrawdownVelocity > 0.001m;

    public decimal GetMaxDrawdownFloor(decimal maxTotalLossPercent) =>
        DrawdownType == "Trailing"
            ? PeakEquity * (1m - maxTotalLossPercent)
            : InitialAccountBalance * (1m - maxTotalLossPercent);

    public decimal GetDailyLossLimit(decimal maxDailyLossPercent) =>
        InitialAccountBalance * (1m - maxDailyLossPercent);
}

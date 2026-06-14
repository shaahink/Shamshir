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
    string DrawdownType);

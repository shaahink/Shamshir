namespace TradingEngine.Domain;

public sealed record NewsBlockWindow(
    string Symbol,
    DayOfWeek? DayOfWeek,
    TimeSpan StartUtc,
    TimeSpan EndUtc,
    string Reason);

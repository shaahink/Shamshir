namespace TradingEngine.Domain;

public sealed record AccountSnapshot(
    DateTime SimTimeUtc,
    decimal Balance,
    decimal Equity,
    decimal FloatingPnL,
    decimal PeakEquity,
    decimal DailyStartEquity,
    decimal DailyDrawdown,
    decimal MaxDrawdown,
    int OpenPositions);

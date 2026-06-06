namespace TradingEngine.Domain;

public record EquitySnapshot(
    DateTime TimestampUtc,
    decimal Balance,
    decimal FloatingPnL,
    decimal Equity,
    decimal PeakEquity,
    decimal DailyStartEquity,
    decimal CurrentDailyDrawdown,
    decimal CurrentMaxDrawdown,
    EngineMode Mode);

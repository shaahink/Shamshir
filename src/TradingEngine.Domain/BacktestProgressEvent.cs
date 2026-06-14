namespace TradingEngine.Domain;

public sealed record BacktestProgressEvent(
    string RunId,
    string EventType,
    string Message,
    DateTime TimestampUtc);

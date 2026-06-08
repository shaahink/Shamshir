namespace TradingEngine.Host;

public sealed record BacktestProgressEvent(
    string RunId,
    string EventType,
    string Message,
    DateTime TimestampUtc);

namespace TradingEngine.Domain;

public sealed record TradeClosed(TradeResult Result, string RunId, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

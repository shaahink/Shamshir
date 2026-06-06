namespace TradingEngine.Domain;

public sealed record TradeClosed(TradeResult Result, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

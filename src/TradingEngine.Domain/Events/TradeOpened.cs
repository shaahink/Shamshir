namespace TradingEngine.Domain;

public sealed record TradeOpened(Position Position, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

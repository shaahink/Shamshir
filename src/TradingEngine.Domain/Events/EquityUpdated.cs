namespace TradingEngine.Domain;

public sealed record EquityUpdated(EquitySnapshot Snapshot, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

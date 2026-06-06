namespace TradingEngine.Domain;

public sealed record EquityUpdated(EquitySnapshot Snapshot, RiskState RiskState, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

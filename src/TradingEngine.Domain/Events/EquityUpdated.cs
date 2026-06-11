namespace TradingEngine.Domain;

public sealed record EquityUpdated(EquitySnapshot Snapshot, ExtendedRiskState RiskState, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

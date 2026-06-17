namespace TradingEngine.Domain;

public sealed record EquityUpdated(EquitySnapshot Snapshot, ExtendedRiskState RiskState, DateTime OccurredAtUtc, string RunId = "") : EngineEvent(OccurredAtUtc);

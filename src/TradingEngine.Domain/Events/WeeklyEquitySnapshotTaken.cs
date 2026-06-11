namespace TradingEngine.Domain.Events;

public sealed record WeeklyEquitySnapshotTaken(
    EquitySnapshot Snapshot, ExtendedRiskState RiskState, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

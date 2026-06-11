namespace TradingEngine.Domain.Events;

public sealed record MonthlyEquitySnapshotTaken(
    EquitySnapshot Snapshot, ExtendedRiskState RiskState, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

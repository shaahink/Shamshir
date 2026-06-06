namespace TradingEngine.Domain;

public sealed record TradeBlocked(TradeIntent Intent, IReadOnlyList<RiskViolation> Violations, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

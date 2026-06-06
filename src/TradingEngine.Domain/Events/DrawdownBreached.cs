namespace TradingEngine.Domain;

public sealed record DrawdownBreached(RiskState State, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

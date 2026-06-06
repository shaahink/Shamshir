namespace TradingEngine.Domain;

public sealed record ForceCloseAllRequested(string Reason, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

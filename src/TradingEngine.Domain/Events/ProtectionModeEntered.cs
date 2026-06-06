namespace TradingEngine.Domain;

public sealed record ProtectionModeEntered(string Reason, DateTime OccurredAtUtc) : EngineEvent(OccurredAtUtc);

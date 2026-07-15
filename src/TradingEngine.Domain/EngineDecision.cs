namespace TradingEngine.Domain;

public sealed record EngineDecision(
    EngineState State,
    IReadOnlyList<EngineEffect> Effects);

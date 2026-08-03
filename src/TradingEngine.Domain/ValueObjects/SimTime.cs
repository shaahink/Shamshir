namespace TradingEngine.Domain;

/// <summary>
/// Simulation time — the engine's own clock, always supplied by the caller and never read from the
/// machine. Exists so <c>TradingEngine.Engine</c>'s public surface can carry a timestamp without
/// carrying <see cref="DateTime"/>: a raw BCL time type on an engine signature is the shape that
/// invites someone to reach for <c>DateTime.UtcNow</c>, and the determinism gate in
/// <c>EnginePurityTests</c> forbids it. Same reasoning as <see cref="Price"/> over decimal.
/// </summary>
public readonly record struct SimTime(DateTime Utc);

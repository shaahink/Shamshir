namespace TradingEngine.Domain;

/// <summary>
/// Production wall-clock that delegates to <see cref="DateTime.UtcNow"/>.
/// The single call-site for the raw static — every other component injects <see cref="IEngineClock"/>
/// so tests and replays can freeze time. Register this as <c>IEngineClock</c> in DI for a
/// non-broker host (e.g. the Web orchestrator).
/// </summary>
public sealed class SystemClock : IEngineClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

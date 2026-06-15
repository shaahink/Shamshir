namespace TradingEngine.Tests.Simulation.Harness;

public sealed class ManualClock : IEngineClock
{
    public DateTime UtcNow { get; set; } = DateTime.UtcNow;
}

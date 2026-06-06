namespace TradingEngine.Domain;

public sealed class StubClock(DateTime initialTime) : IEngineClock
{
    public DateTime UtcNow { get; private set; } = initialTime;

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
    public void Set(DateTime value) => UtcNow = value;
}

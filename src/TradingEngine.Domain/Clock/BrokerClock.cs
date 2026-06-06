namespace TradingEngine.Domain;

public sealed class BrokerClock(IBrokerAdapter adapter) : IEngineClock
{
    public DateTime UtcNow => adapter.IsConnected
        ? adapter.BrokerTimeUtc
        : DateTime.UtcNow;
}

namespace TradingEngine.Domain;

public interface IEngineClock
{
    DateTime UtcNow { get; }
}

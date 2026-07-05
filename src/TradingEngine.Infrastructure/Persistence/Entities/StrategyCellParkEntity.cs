namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class StrategyCellParkEntity
{
    public Guid Id { get; set; }
    public string StrategyId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Timeframe { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime ParkedAtUtc { get; set; } = DateTime.UtcNow;
}

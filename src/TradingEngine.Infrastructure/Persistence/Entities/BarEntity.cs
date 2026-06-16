namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class BarEntity
{
    public Guid Id { get; set; }
    public string RunId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Timeframe { get; set; } = "";
    public DateTime OpenTimeUtc { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public double Volume { get; set; }
}

namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class EquitySnapshotEntity
{
    public Guid Id { get; set; }
    public DateTime TimestampUtc { get; set; }
    public decimal Balance { get; set; }
    public decimal FloatingPnL { get; set; }
    public decimal Equity { get; set; }
    public decimal PeakEquity { get; set; }
    public decimal DailyStartEquity { get; set; }
    public decimal CurrentDailyDrawdown { get; set; }
    public decimal CurrentMaxDrawdown { get; set; }
    public string Mode { get; set; } = "";
    public string Type { get; set; } = "Tick";
}

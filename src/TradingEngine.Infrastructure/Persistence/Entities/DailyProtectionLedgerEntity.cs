namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class DailyProtectionLedgerEntity
{
    public Guid Id { get; set; }
    public string? RunId { get; set; }
    public DateTime Date { get; set; }
    public decimal StartEquity { get; set; }
    public decimal MinEquity { get; set; }
    public decimal EndEquity { get; set; }
    public double MaxDailyDdUsedFraction { get; set; }
    public string FinalGovernorState { get; set; } = "Normal";
    public bool BreachOccurred { get; set; }
    public int TradesOpened { get; set; }
    public int TradesClosed { get; set; }
    public int SignalsBlocked { get; set; }
    public ICollection<ProtectionLedgerEntryEntity> Entries { get; set; } = [];
}

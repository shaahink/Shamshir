namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class ProtectionLedgerEntryEntity
{
    public Guid Id { get; set; }
    public Guid LedgerId { get; set; }
    public DailyProtectionLedgerEntity Ledger { get; set; } = null!;
    public DateTime AtUtc { get; set; }
    public string Category { get; set; } = "";
    public string Reason { get; set; } = "";
    public decimal EquityAtTime { get; set; }
    public double DailyDdUsedFraction { get; set; }
}

namespace TradingEngine.Web.Dtos.Protection;

public sealed record ProtectionEntryResponse
{
    public DateTime AtUtc { get; init; }
    public required string Category { get; init; }
    public required string Reason { get; init; }
    public decimal EquityAtTime { get; init; }
    public double DailyDdUsedFraction { get; init; }
}

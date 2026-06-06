namespace TradingEngine.Domain;

public record Tick(
    Symbol Symbol,
    decimal Bid,
    decimal Ask,
    DateTime TimestampUtc)
{
    public decimal Spread => Ask - Bid;
    public decimal Mid => (Bid + Ask) / 2m;
}

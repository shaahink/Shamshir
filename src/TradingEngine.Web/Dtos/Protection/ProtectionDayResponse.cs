namespace TradingEngine.Web.Dtos.Protection;

public sealed record ProtectionDayResponse
{
    public Guid Id { get; init; }
    public DateTime Date { get; init; }
    public decimal StartEquity { get; init; }
    public decimal MinEquity { get; init; }
    public decimal EndEquity { get; init; }
    public double MaxDailyDdUsedFraction { get; init; }
    public string? FinalGovernorState { get; init; }
    public bool BreachOccurred { get; init; }
    public int TradesOpened { get; init; }
    public int TradesClosed { get; init; }
    public int SignalsBlocked { get; init; }
}

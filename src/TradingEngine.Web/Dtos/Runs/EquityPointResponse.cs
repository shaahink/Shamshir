namespace TradingEngine.Web.Dtos.Runs;

public sealed record EquityPointResponse
{
    public DateTime TimestampUtc { get; init; }
    public decimal Equity { get; init; }
    public decimal Balance { get; init; }
}

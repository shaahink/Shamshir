namespace TradingEngine.Web.Dtos.Runs;

public sealed record DailyPnlResponse
{
    public required string Date { get; init; }
    public decimal PnL { get; init; }
}

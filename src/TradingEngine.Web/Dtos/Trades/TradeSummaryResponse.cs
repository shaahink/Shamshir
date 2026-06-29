namespace TradingEngine.Web.Dtos.Trades;

public sealed record TradeSummaryResponse
{
    public required Guid Id { get; init; }
    public required Guid PositionId { get; init; }
    public Guid OrderId { get; init; }
    public string? RunId { get; init; }
    public required string Symbol { get; init; }
    public required string Direction { get; init; }
    public decimal Lots { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public DateTime ClosedAtUtc { get; init; }
    public decimal GrossPnLAmount { get; init; }
    public decimal CommissionAmount { get; init; }
    public decimal SwapAmount { get; init; }
    public decimal NetPnLAmount { get; init; }
    public double PnLPips { get; init; }
    public double RMultiple { get; init; }
    public double MaxAdverseExcursion { get; init; }
    public double MaxFavorableExcursion { get; init; }
    public required string ExitReason { get; init; }
    public required string StrategyId { get; init; }
    public double DurationSeconds { get; init; }
    public string? EntryType { get; init; }
}

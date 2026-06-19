namespace TradingEngine.Web.Dtos.Trades;

public sealed record TradeSummaryResponse
{
    public required Guid Id { get; init; }
    public required Guid PositionId { get; init; }
    public required string Symbol { get; init; }
    public required string Direction { get; init; }
    public decimal Lots { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public DateTime ClosedAtUtc { get; init; }
    public decimal NetPnLAmount { get; init; }
    public double PnLPips { get; init; }
    public double RMultiple { get; init; }
    public required string ExitReason { get; init; }
    public required string StrategyId { get; init; }
}

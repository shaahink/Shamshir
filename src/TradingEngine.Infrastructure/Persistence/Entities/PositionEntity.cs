namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class PositionEntity
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";
    public decimal Lots { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal CurrentStopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public string StrategyId { get; set; } = "";
    public string? ExitReason { get; set; }
}

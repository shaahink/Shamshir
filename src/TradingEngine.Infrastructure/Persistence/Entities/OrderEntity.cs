namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class OrderEntity : IAuditableEntity
{
    public Guid Id { get; set; }
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";
    public string OrderType { get; set; } = "";
    public string State { get; set; } = "";
    public decimal RequestedLots { get; set; }
    public decimal? FillPrice { get; set; }
    public decimal FilledLots { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? FilledAtUtc { get; set; }
    public string? LimitPrice { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public string StrategyId { get; set; } = "";
    public string RiskProfileId { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime UpdatedAtUtc { get; set; }
}

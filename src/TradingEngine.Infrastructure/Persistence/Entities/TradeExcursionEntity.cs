namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class TradeExcursionEntity : IAuditableEntity
{
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public Guid Id { get; set; }
    public string RunId { get; set; } = "";
    public Guid PositionId { get; set; }
    public string PathJson { get; set; } = "";
}

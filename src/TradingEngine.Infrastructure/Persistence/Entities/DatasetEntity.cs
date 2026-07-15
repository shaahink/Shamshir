namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class DatasetEntity : IAuditableEntity
{
    public string Id { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Symbols { get; set; } = "[]";
    public string Timeframes { get; set; } = "[]";
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public string Granularity { get; set; } = "Bar";
    public long RowCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

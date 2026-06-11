namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class PipelineEventEntity
{
    public Guid Id { get; set; }
    public string RunId { get; set; } = "";
    public long Seq { get; set; }
    public string Stage { get; set; } = "";
    public string? CorrelationId { get; set; }
    public DateTime SimTimeUtc { get; set; }
    public DateTime WallTimeUtc { get; set; }
    public string DetailJson { get; set; } = "{}";
}

namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class EngineEventEntity : IAuditableEntity
{
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public Guid Id { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; }
}

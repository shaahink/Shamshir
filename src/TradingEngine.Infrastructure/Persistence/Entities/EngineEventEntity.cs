namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class EngineEventEntity
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; }
}

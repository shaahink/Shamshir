namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class VenueSessionEntity : IAuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RunId { get; set; } = "";
    public string Venue { get; set; } = "ctrader";
    public string Event { get; set; } = "";
    public string? Detail { get; set; }
    public DateTime OccurredAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

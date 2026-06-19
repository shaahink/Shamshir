namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class GovernorOptionsEntity
{
    public string Id { get; set; } = "default";
    public string Json { get; set; } = "{}";
    public DateTime UpdatedAtUtc { get; set; }
}

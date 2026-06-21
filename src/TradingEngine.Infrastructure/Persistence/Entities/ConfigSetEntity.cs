namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class ConfigSetEntity : IAuditableEntity
{
    public string Id { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Json { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

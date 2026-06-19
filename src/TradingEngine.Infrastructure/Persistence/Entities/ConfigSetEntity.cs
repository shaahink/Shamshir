namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class ConfigSetEntity
{
    public string Id { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string Json { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

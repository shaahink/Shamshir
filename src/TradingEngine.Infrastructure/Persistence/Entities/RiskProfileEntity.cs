namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class RiskProfileEntity
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Json { get; set; } = "{}";
    public DateTime UpdatedAtUtc { get; set; }
}

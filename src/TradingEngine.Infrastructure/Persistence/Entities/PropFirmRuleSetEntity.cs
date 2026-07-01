namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class PropFirmRuleSetEntity : IAuditableEntity
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Json { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

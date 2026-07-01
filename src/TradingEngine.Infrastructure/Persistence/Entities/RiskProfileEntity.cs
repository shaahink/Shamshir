namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class RiskProfileEntity : IAuditableEntity
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Json { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }   // iter-38 D5
    public DateTime UpdatedAtUtc { get; set; }
}

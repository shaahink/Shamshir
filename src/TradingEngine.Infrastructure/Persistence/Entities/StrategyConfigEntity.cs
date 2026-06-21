namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class StrategyConfigEntity : IAuditableEntity
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; }
    public string DefaultSymbols { get; set; } = "[]";
    public string Timeframe { get; set; } = "H1";
    public string RiskProfileId { get; set; } = "";
    public string ParametersJson { get; set; } = "{}";
    public string? PositionManagementJson { get; set; }
    public string? OrderEntryJson { get; set; }
    public string? RegimeFilterJson { get; set; }
    public string? ReentryJson { get; set; }
    public DateTime CreatedAtUtc { get; set; }   // iter-38 D5
    public DateTime UpdatedAtUtc { get; set; }
}

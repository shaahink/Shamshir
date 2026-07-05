namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class StrategyConfigEntity : IAuditableEntity
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; }
    public string RiskProfileId { get; set; } = "";
    public string ParametersJson { get; set; } = "{}";
    public string? PositionManagementJson { get; set; }
    public string? OrderEntryJson { get; set; }
    public string? RegimeFilterJson { get; set; }
    public string? ReentryJson { get; set; }

    // P2.5: falsifiable-hypothesis metadata — forces every strategy to state its claim, used by P4's
    // frequency reality check (needed trades vs actual OOS trades/30 days).
    public string? Thesis { get; set; }
    public int? ExpectedTradesPerWeek { get; set; }
    public int? ExpectedHoldBars { get; set; }

    public int Version { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

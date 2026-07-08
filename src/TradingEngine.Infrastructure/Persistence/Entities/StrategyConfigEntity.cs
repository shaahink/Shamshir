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

    // P1.2 (F9): content hash of the source config/strategies/*.json at the last sync-from-disk, and when
    // that sync happened. Drift detection compares the current file hash against SeededHash; a UI/hand edit
    // is inferred from UpdatedAtUtc > SeededAtUtc (so an edited-on-disk file is NOT clobbered when the row
    // was hand-edited since the last seed). Null SeededHash = never synced from a file yet (pre-M42 rows).
    public string? SeededHash { get; set; }
    public DateTime? SeededAtUtc { get; set; }
}

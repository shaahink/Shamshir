namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class RiskProfileEntity : IAuditableEntity
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Json { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }   // iter-38 D5
    public DateTime UpdatedAtUtc { get; set; }

    // P1.2 (F9): content hash of the source config/risk-profiles/*.json at the last sync-from-disk (see
    // StrategyConfigEntity for the drift/hand-edit policy).
    public string? SeededHash { get; set; }
    public DateTime? SeededAtUtc { get; set; }
}

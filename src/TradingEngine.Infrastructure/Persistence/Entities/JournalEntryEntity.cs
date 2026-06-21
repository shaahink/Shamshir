namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class JournalEntryEntity : IAuditableEntity
{
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public string RunId { get; set; } = "";
    public long Seq { get; set; }
    public DateTime SimTimeUtc { get; set; }
    public string EventKind { get; set; } = "";
    public string EventJson { get; set; } = "{}";
    public string EffectKinds { get; set; } = "[]";
    public string EffectsJson { get; set; } = "[]";
    public string RiskJson { get; set; } = "{}";
    public string? Regime { get; set; }
    public string? DecisionReason { get; set; }
    public string VerdictsJson { get; set; } = "[]";
}

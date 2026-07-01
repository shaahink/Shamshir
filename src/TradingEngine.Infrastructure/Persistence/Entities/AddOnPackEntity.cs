namespace TradingEngine.Infrastructure.Persistence.Entities;

/// <summary>
/// iter-38 (Stream PK1). Persisted form of a reusable <c>AddOnPack</c> (owner decision D1). The add-on bundle
/// is stored as a JSON blob of <c>PositionManagementOptions</c> (same shape as a strategy's own add-ons), so a
/// pack and a strategy share one editor and one merge path. Implements <see cref="IAuditableEntity"/> (D5) —
/// the reference example for the T1 retrofit of the other entities.
/// </summary>
public sealed class AddOnPackEntity : IAuditableEntity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>JSON of <c>PositionManagementOptions</c> (the breakeven/trailing/partial/ride/dynamic add-ons).</summary>
    public string AddOnsJson { get; set; } = "{}";

    public bool RegimeDetectionEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

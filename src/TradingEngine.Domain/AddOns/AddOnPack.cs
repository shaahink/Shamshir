namespace TradingEngine.Domain;

/// <summary>
/// iter-38 (owner decision D1). A reusable, named bundle of add-ons that can be attached to ANY strategy or
/// run by id. The pack's <see cref="AddOns"/> are the enrichment fields of <see cref="PositionManagementOptions"/>
/// (breakeven / trailing / partial-tp / ride / dynamic-sl-tp) plus the regime-detection toggle. When a run (or
/// strategy) names a pack, the pack REPLACES that strategy's default add-ons for the run; the mandatory baseline
/// SL/TP still comes from the strategy (D4). A strategy/run naming no pack and carrying no add-ons trades the
/// baseline only.
///
/// The pack payload deliberately reuses <see cref="PositionManagementOptions"/> so a pack and a strategy's own
/// add-ons share one shape (and one editor / one merge path in <c>EffectiveConfigResolver</c>).
/// </summary>
public sealed record AddOnPack(
    string Id,
    string Name,
    string? Description,
    PositionManagementOptions AddOns,
    bool RegimeDetectionEnabled = true);

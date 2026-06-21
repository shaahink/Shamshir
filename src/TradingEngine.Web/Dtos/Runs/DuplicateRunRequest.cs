namespace TradingEngine.Web.Dtos.Runs;

/// <summary>
/// "Duplicate with changes" (iter-36 K6 / iter-37 F3): re-run a finished run over the SAME dataset window
/// with an optionally-changed strategy set / risk profile / per-strategy overrides. The new run keeps the
/// source's DatasetId, gets a fresh ConfigSetId, and records ParentRunId = source for lineage. All fields
/// optional — omitting everything re-runs the source identically (same dataset, same config → deterministic).
/// </summary>
public sealed record DuplicateRunRequest
{
    public List<string>? StrategyIds { get; init; }
    public string? RiskProfileId { get; init; }
    public string? Venue { get; init; }
    public Dictionary<string, Dictionary<string, object>>? StrategyOverrides { get; init; }

    /// <summary>iter-38 (PK3 / D1). Apply a reusable add-on pack to the duplicated run.</summary>
    public string? UsePackId { get; init; }

    /// <summary>iter-38 (R1 / D3). Force regime detection off for the duplicated run.</summary>
    public bool DisableRegime { get; init; }
}

namespace TradingEngine.Domain;

public record RegimeFilterOptions
{
    /// <summary>iter-38 (owner decision D3 / Stream R1). Master switch for regime detection on this strategy.
    /// When false, <c>BarEvaluator</c> SKIPS <c>regimeDetector.Detect</c>, treats the regime as bypassed, and
    /// <see cref="Allows"/> short-circuits to allow-all (the per-regime flags below are ignored). A run can
    /// force this off for every strategy via the run-level master (StartRunRequest.DisableRegime).</summary>
    public bool DetectionEnabled { get; init; } = true;

    public bool AllowTrending { get; init; } = true;
    public bool AllowRanging { get; init; } = true;
    public bool AllowHighVolatility { get; init; } = true;
    public bool AllowLowVolatility { get; init; } = true;
    public bool AllowUnknown { get; init; } = true;
}

public static class RegimeFilterExtensions
{
    public static bool Allows(this RegimeFilterOptions filter, MarketRegime regime)
    {
        // iter-38 R1: detection off ⇒ regime is not a gate (allow-all). The agent must ALSO short-circuit the
        // detect call itself in BarEvaluator for cost/journal clarity; this guard keeps the filter consistent.
        if (!filter.DetectionEnabled) return true;

        return regime switch
        {
            MarketRegime.Trending => filter.AllowTrending,
            MarketRegime.Ranging => filter.AllowRanging,
            MarketRegime.HighVolatility => filter.AllowHighVolatility,
            MarketRegime.LowVolatility => filter.AllowLowVolatility,
            _ => filter.AllowUnknown,
        };
    }
}

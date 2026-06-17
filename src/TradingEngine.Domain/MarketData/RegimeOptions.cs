namespace TradingEngine.Domain;

/// <summary>
/// Tunables for <c>AtrBasedRegimeDetector</c>. Previously these lived as magic numbers in the
/// detector; they are now config-driven (config/regime.json) so the regime classification can be
/// tuned without a rebuild. All thresholds have sane forex-H1 defaults.
/// </summary>
public sealed record RegimeOptions
{
    /// <summary>ATR period used for the current-volatility reading and the rolling baseline.</summary>
    public int AtrPeriod { get; init; } = 14;

    /// <summary>ADX period used to separate trending vs ranging.</summary>
    public int AdxPeriod { get; init; } = 14;

    /// <summary>Bars of history required before a regime (other than Unknown) is reported.</summary>
    public int MinBars { get; init; } = 100;

    /// <summary>Lookback (in bars) for the average-true-range baseline the current ATR is compared to.</summary>
    public int BaselineLookback { get; init; } = 100;

    /// <summary>currentATR / baseline at or above this ⇒ HighVolatility.</summary>
    public double HighVolatilityAtrRatio { get; init; } = 2.5;

    /// <summary>currentATR / baseline at or below this ⇒ LowVolatility.</summary>
    public double LowVolatilityAtrRatio { get; init; } = 0.4;

    /// <summary>ADX at or above this ⇒ Trending.</summary>
    public double TrendingAdxThreshold { get; init; } = 25.0;

    /// <summary>ADX at or below this (and &gt; 0) ⇒ Ranging.</summary>
    public double RangingAdxThreshold { get; init; } = 18.0;
}

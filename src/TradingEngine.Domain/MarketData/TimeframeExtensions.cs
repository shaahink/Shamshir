namespace TradingEngine.Domain;

/// <summary>
/// Canonical bar-duration for a <see cref="Timeframe"/>. Several call sites (BarEvaluator,
/// KernelTrailingEvaluator, replay adapters) each hand-rolled this switch; new market-data/tape code
/// uses this single helper. Existing duplicates are intentionally left untouched to avoid golden drift.
/// </summary>
public static class TimeframeExtensions
{
    public static TimeSpan ToTimeSpan(this Timeframe tf) => tf switch
    {
        Timeframe.M1 => TimeSpan.FromMinutes(1),
        Timeframe.M5 => TimeSpan.FromMinutes(5),
        Timeframe.M15 => TimeSpan.FromMinutes(15),
        Timeframe.M30 => TimeSpan.FromMinutes(30),
        Timeframe.H1 => TimeSpan.FromHours(1),
        Timeframe.H4 => TimeSpan.FromHours(4),
        Timeframe.D1 => TimeSpan.FromDays(1),
        Timeframe.W1 => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(1),
    };
}

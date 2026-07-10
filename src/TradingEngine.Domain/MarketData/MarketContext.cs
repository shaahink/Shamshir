namespace TradingEngine.Domain;

public record MarketContext(
    Symbol Symbol,
    Tick LatestTick,
    IReadOnlyDictionary<Timeframe, IReadOnlyList<Bar>> Bars,
    IReadOnlyDictionary<string, double> IndicatorValues,
    DateTime EngineTimeUtc,
    // P2.1: per-key indicator history (oldest first, latest last), capped at 64 values. Null in call sites
    // that don't populate it (older tests) -- use MarketContextExtensions.GetSeries for a null-safe read.
    IReadOnlyDictionary<string, IReadOnlyList<double>>? IndicatorSeries = null);

public static class MarketContextExtensions
{
    private static readonly IReadOnlyList<double> Empty = [];

    /// <summary>The indicator series for <paramref name="key"/>, oldest first. Empty (never null) if the
    /// context wasn't given a series map or the key has no history yet.</summary>
    public static IReadOnlyList<double> GetSeries(this MarketContext context, string key)
        => context.IndicatorSeries?.GetValueOrDefault(key) ?? Empty;
}

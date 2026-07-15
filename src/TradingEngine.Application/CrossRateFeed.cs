namespace TradingEngine.Application;

/// <summary>
/// Advances a <see cref="CrossRateStore"/> along pre-loaded USD-leg series as sim-time moves.
///
/// The gap this closes: rates used to be refreshed only when a run happened to stream GBPUSD or USDJPY
/// bars (<c>EngineRunner.UpdateCrossRates</c>). A run on EURJPY streams neither, so every pip value came
/// off a stale literal. The series are resolved and loaded up-front by the caller that owns market data
/// (see <c>CrossRateSeriesLoader</c>), so a leg we cannot source fails the run at start rather than
/// silently pricing at the wrong rate — a wrong cross rate is a wrong lot size.
/// </summary>
public sealed class CrossRateFeed(
    CrossRateStore store,
    IReadOnlyDictionary<string, IReadOnlyList<CrossRatePoint>> series)
{
    private readonly Dictionary<string, int> _cursors =
        series.ToDictionary(kv => kv.Key, _ => 0, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Currencies => (IReadOnlyCollection<string>)series.Keys;

    /// <summary>Move every leg to its most recent observation at or before <paramref name="simTimeUtc"/>.</summary>
    public void Advance(DateTime simTimeUtc)
    {
        foreach (var (currency, points) in series)
        {
            if (points.Count == 0) continue;

            var cursor = _cursors[currency];
            while (cursor + 1 < points.Count && points[cursor + 1].AtUtc <= simTimeUtc)
                cursor++;
            _cursors[currency] = cursor;

            store.SetUsdPerUnit(currency, points[cursor].UsdPerUnit);
        }
    }
}

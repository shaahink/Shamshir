namespace TradingEngine.Infrastructure.Indicators;

/// <summary>
/// Classifies the market regime from the bar window it is handed. It computes ATR and ADX itself
/// (via <see cref="IIndicatorService"/>) rather than reading a shared indicator dictionary — the old
/// implementation looked up symbol-prefixed keys ("EURUSD:ATR_14") that the snapshot never populated
/// AND depended on some active strategy requesting ADX, so it silently returned Unknown on every bar.
/// Being self-contained means the regime is correct regardless of which strategies are active.
/// </summary>
public sealed class AtrBasedRegimeDetector : IRegimeDetector
{
    private readonly IIndicatorService _indicators;
    private readonly RegimeOptions _options;

    public AtrBasedRegimeDetector(IIndicatorService indicators, RegimeOptions? options = null)
    {
        _indicators = indicators;
        _options = options ?? new RegimeOptions();
    }

    public MarketRegime Detect(Symbol symbol, IReadOnlyList<Bar> bars,
        IReadOnlyDictionary<string, double> indicators)
    {
        if (bars.Count < _options.MinBars) return MarketRegime.Unknown;

        var currentAtr = _indicators.Atr(bars, _options.AtrPeriod);
        var adx = _indicators.Adx(bars, _options.AdxPeriod);

        // ATR baseline: average true range over the recent window the current ATR is compared to.
        var lookback = Math.Min(bars.Count, _options.BaselineLookback);
        var sum = 0.0;
        for (int i = bars.Count - lookback; i < bars.Count; i++)
        {
            var high = (double)bars[i].High;
            var low = (double)bars[i].Low;
            var prevClose = (double)(i > 0 ? bars[i - 1].Close : bars[i].Close);
            var tr = Math.Max(high - low,
                Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            sum += tr;
        }
        var baseline = lookback > 0 ? sum / lookback : 0;

        if (currentAtr > 0 && baseline > 0)
        {
            var atrRatio = currentAtr / baseline;
            if (atrRatio >= _options.HighVolatilityAtrRatio) return MarketRegime.HighVolatility;
            if (atrRatio <= _options.LowVolatilityAtrRatio) return MarketRegime.LowVolatility;
        }

        if (adx >= _options.TrendingAdxThreshold) return MarketRegime.Trending;
        if (adx > 0 && adx <= _options.RangingAdxThreshold) return MarketRegime.Ranging;

        return MarketRegime.Unknown;
    }
}

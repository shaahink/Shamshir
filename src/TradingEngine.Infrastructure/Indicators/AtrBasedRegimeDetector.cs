namespace TradingEngine.Infrastructure.Indicators;

public sealed class AtrBasedRegimeDetector : IRegimeDetector
{
    public MarketRegime Detect(Symbol symbol, IReadOnlyList<Bar> bars,
        IReadOnlyDictionary<string, double> indicators)
    {
        if (bars.Count < 100) return MarketRegime.Unknown;

        var symbolPrefix = $"{symbol}:";
        indicators.TryGetValue($"{symbolPrefix}ATR_14", out var currentAtr);
        indicators.TryGetValue($"{symbolPrefix}ADX_14", out var adx);

        // Compute ATR baseline from recent bars
        var lookback = Math.Min(bars.Count, 100);
        var sum = 0.0;
        var count = 0;
        for (int i = bars.Count - lookback; i < bars.Count; i++)
        {
            var high = (double)bars[i].High;
            var low = (double)bars[i].Low;
            var prevClose = (double)(i > 0 ? bars[i - 1].Close : bars[i].Close);
            var tr = Math.Max(high - low,
                Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            sum += tr;
            count++;
        }
        var baseline = count > 0 ? sum / count : 0;

        if (currentAtr > 0 && baseline > 0)
        {
            var atrRatio = currentAtr / baseline;
            if (atrRatio >= 2.5) return MarketRegime.HighVolatility;
            if (atrRatio <= 0.4) return MarketRegime.LowVolatility;
        }

        if (adx >= 25.0) return MarketRegime.Trending;
        if (adx <= 18.0 && adx > 0) return MarketRegime.Ranging;

        return MarketRegime.Unknown;
    }
}

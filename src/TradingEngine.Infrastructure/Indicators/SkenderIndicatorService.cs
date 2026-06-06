using Skender.Stock.Indicators;

namespace TradingEngine.Infrastructure.Indicators;

public sealed class SkenderIndicatorService : IIndicatorService
{
    private readonly IndicatorCache _cache = new();

    public double Atr(IReadOnlyList<Bar> bars, int period)
    {
        var key = _cache.BuildKey(bars[0].Symbol, bars[0].Timeframe, "ATR", period, bars.Count);
        var cached = _cache.Get(key);
        if (cached.HasValue) return cached.Value;

        var quotes = bars.Select(b => new SkenderQuote(b)).ToList();
        var result = quotes.GetAtr(period).LastOrDefault()?.Atr ?? 0;
        _cache.Set(key, result);
        return result;
    }

    public double Ema(IReadOnlyList<Bar> bars, int period)
    {
        var key = _cache.BuildKey(bars[0].Symbol, bars[0].Timeframe, "EMA", period, bars.Count);
        var cached = _cache.Get(key);
        if (cached.HasValue) return cached.Value;

        var quotes = bars.Select(b => new SkenderQuote(b)).ToList();
        var result = quotes.GetEma(period).LastOrDefault()?.Ema ?? 0;
        _cache.Set(key, result);
        return result;
    }

    public double Sma(IReadOnlyList<Bar> bars, int period)
    {
        var key = _cache.BuildKey(bars[0].Symbol, bars[0].Timeframe, "SMA", period, bars.Count);
        var cached = _cache.Get(key);
        if (cached.HasValue) return cached.Value;

        var quotes = bars.Select(b => new SkenderQuote(b)).ToList();
        var result = quotes.GetSma(period).LastOrDefault()?.Sma ?? 0;
        _cache.Set(key, result);
        return result;
    }

    public (double Upper, double Middle, double Lower) BollingerBands(IReadOnlyList<Bar> bars, int period, double stdDev)
    {
        var quotes = bars.Select(b => new SkenderQuote(b)).ToList();
        var bb = quotes.GetBollingerBands(period, stdDev).LastOrDefault();
        return (bb?.UpperBand ?? 0, bb?.Sma ?? 0, bb?.LowerBand ?? 0);
    }

    public double Rsi(IReadOnlyList<Bar> bars, int period)
    {
        var quotes = bars.Select(b => new SkenderQuote(b)).ToList();
        return quotes.GetRsi(period).LastOrDefault()?.Rsi ?? 50;
    }

    public void InvalidateCache() => _cache.Clear();
}

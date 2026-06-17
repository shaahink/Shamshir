using Skender.Stock.Indicators;

namespace TradingEngine.Infrastructure.Indicators;

// Stateless: the previous internal cache keyed by bars.Count was unsafe. The bar window is capped
// (TradingLoop trims to 500), so once saturated every bar produced the SAME key and ATR/EMA/SMA were
// served stale; as a process-lifetime singleton it also leaked values across backtest runs. Indicator
// de-duplication now happens per-bar in IndicatorSnapshotService, which is both correct and cheap.
public sealed class SkenderIndicatorService : IIndicatorService
{
    public double Atr(IReadOnlyList<Bar> bars, int period)
    {
        var quotes = bars.Select(b => new SkenderQuote(b)).ToList();
        return quotes.GetAtr(period).LastOrDefault()?.Atr ?? 0;
    }

    public double Ema(IReadOnlyList<Bar> bars, int period)
    {
        var quotes = bars.Select(b => new SkenderQuote(b)).ToList();
        return quotes.GetEma(period).LastOrDefault()?.Ema ?? 0;
    }

    public double Sma(IReadOnlyList<Bar> bars, int period)
    {
        var quotes = bars.Select(b => new SkenderQuote(b)).ToList();
        return quotes.GetSma(period).LastOrDefault()?.Sma ?? 0;
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

    public double Adx(IReadOnlyList<Bar> bars, int period)
    {
        var quotes = bars.Select(b => new SkenderQuote(b)).ToList();
        return quotes.GetAdx(period).LastOrDefault()?.Adx ?? 0;
    }

    public IndMacdResult Macd(IReadOnlyList<Bar> bars, int fast, int slow, int signal)
    {
        var quotes = bars.Select(b => new SkenderQuote(b)).ToList();
        var last = quotes.GetMacd(fast, slow, signal).LastOrDefault();
        return new IndMacdResult(last?.Macd ?? 0, last?.Signal ?? 0, last?.Histogram ?? 0);
    }

    public IndSuperTrendResult SuperTrend(IReadOnlyList<Bar> bars, int period, double multiplier)
    {
        var quotes = bars.Select(b => new SkenderQuote(b)).ToList();
        var last = quotes.GetSuperTrend(period, multiplier).LastOrDefault();
        if (last is null) return new IndSuperTrendResult(0, 0);
        var direction = last.UpperBand is not null ? -1.0 : last.LowerBand is not null ? 1.0 : 0;
        return new IndSuperTrendResult((double)(last.SuperTrend ?? 0), direction);
    }
}

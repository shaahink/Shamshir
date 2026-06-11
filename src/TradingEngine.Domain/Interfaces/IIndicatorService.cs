namespace TradingEngine.Domain;

public interface IIndicatorService
{
    double Atr(IReadOnlyList<Bar> bars, int period);
    double Ema(IReadOnlyList<Bar> bars, int period);
    double Sma(IReadOnlyList<Bar> bars, int period);
    (double Upper, double Middle, double Lower) BollingerBands(IReadOnlyList<Bar> bars, int period, double stdDev);
    double Rsi(IReadOnlyList<Bar> bars, int period);
    double Adx(IReadOnlyList<Bar> bars, int period);
    IndMacdResult Macd(IReadOnlyList<Bar> bars, int fast, int slow, int signal);
    IndSuperTrendResult SuperTrend(IReadOnlyList<Bar> bars, int period, double multiplier);
}

public readonly record struct IndMacdResult(double MacdLine, double Signal, double Histogram);
public readonly record struct IndSuperTrendResult(double Line, double Direction);

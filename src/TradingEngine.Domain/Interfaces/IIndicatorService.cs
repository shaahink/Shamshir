namespace TradingEngine.Domain;

public interface IIndicatorService
{
    double Atr(IReadOnlyList<Bar> bars, int period);
    double Ema(IReadOnlyList<Bar> bars, int period);
    double Sma(IReadOnlyList<Bar> bars, int period);
    (double Upper, double Middle, double Lower) BollingerBands(IReadOnlyList<Bar> bars, int period, double stdDev);
    double Rsi(IReadOnlyList<Bar> bars, int period);
}

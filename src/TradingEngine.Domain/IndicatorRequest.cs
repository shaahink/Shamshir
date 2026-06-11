namespace TradingEngine.Domain;

public sealed record IndicatorRequest(string Key, IndicatorType Type, int Period,
    double StdDev = 2.0, Timeframe Timeframe = Timeframe.H1)
{
    public int Param1 { get; init; }
    public double Param2 { get; init; }
}

public enum IndicatorType { Ema, Sma, Rsi, Atr, BollingerBands, Macd, Adx, SuperTrend }

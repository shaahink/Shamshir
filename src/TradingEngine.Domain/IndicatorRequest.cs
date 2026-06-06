namespace TradingEngine.Domain;

public sealed record IndicatorRequest(string Key, IndicatorType Type, int Period, double StdDev = 2.0);

public enum IndicatorType { Atr, Ema, Sma, Rsi, BollingerBands, Macd }

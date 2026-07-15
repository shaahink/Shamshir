namespace TradingEngine.Web.Api;

/// <summary>
/// P6: static metadata map — entry rule + exit formula per strategy.
/// Lives next to the strategy classes; no schema change.
/// </summary>
public static class StrategyMetadataMap
{
    private static readonly Dictionary<string, StrategyMetadata> _entries = new()
    {
        ["super-trend"] = new(
            "Close crosses SuperTrend flip",
            "1.5×ATR trailing stop · TP: 2R · Market"),
        ["trend-breakout"] = new(
            "Break of 20-bar high/low with ADX > 20",
            "1×ATR fixed stop · TP: 2R · Market"),
        ["mean-reversion"] = new(
            "RSI(14) oversold/overbought bounce with BB confirmation",
            "1.5×ATR stop · TP: 2R · Market"),
        ["ema-alignment"] = new(
            "EMA 10/20/50 bullish/bearish alignment",
            "1.5×ATR stop · TP: 2R · Market"),
        ["session-breakout"] = new(
            "Break of Asian/European session range",
            "1×ATR stop · TP: 2R · Market"),
        ["macd-momentum"] = new(
            "MACD(12,26,9) signal cross with price above/below EMA(20)",
            "1.5×ATR stop · TP: 2R · Market"),
        ["rsi-divergence"] = new(
            "RSI(14) divergence from price with trend confirmation",
            "1.5×ATR stop · TP: 2R · Market"),
        // P2.5 drive-by fix: the real [StrategyId] is "bb-squeeze" (see BollingerSqueezeStrategy.cs) —
        // this entry never matched anything looked up by StrategiesController.
        ["bb-squeeze"] = new(
            "Bollinger Band squeeze breakout with volume confirmation",
            "1×ATR stop · TP: 2R · Market"),
        ["mtf-trend"] = new(
            "Multi-timeframe trend alignment (H4 + H1 + M15)",
            "1.5×ATR stop · TP: 2R · Market"),
    };

    public static StrategyMetadata? Get(string strategyId) =>
        _entries.TryGetValue(strategyId, out var m) ? m : null;
}

public record StrategyMetadata(string EntryRule, string ExitFormula);

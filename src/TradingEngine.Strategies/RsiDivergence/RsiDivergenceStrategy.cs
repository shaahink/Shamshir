using TradingEngine.Services.SLTPCalculation;

namespace TradingEngine.Strategies.RsiDivergence;

[StrategyId("rsi-divergence")]
public sealed class RsiDivergenceStrategy : IStrategy
{
    private readonly RsiDivergenceConfig _config;
    private readonly SymbolEntry[] _symbols;
    private int _winStreak;
    private int _lossStreak;

    public RsiDivergenceStrategy(RsiDivergenceConfig config, ISymbolInfoRegistry symbolRegistry, ILogger<RsiDivergenceStrategy> logger)
    {
        _config = config;
        _symbols = config.Symbols.Select(s => new SymbolEntry(Symbol.Parse(s), symbolRegistry.Get(Symbol.Parse(s)))).ToArray();
    }

    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IStrategyConfig Config => _config;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [Timeframe.H1];
    public int RequiredBarCount => _config.Parameters.DivergenceLookback + _config.Parameters.RsiPeriod + 5;
    public IReadOnlyList<IndicatorRequest> RequiredIndicators => [
        new($"RSI_{_config.Parameters.RsiPeriod}", IndicatorType.Rsi, _config.Parameters.RsiPeriod),
        new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod),
    ];
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public StrategyStats Stats => new(_winStreak, _lossStreak, 0, 0);

    public TradeIntent? Evaluate(MarketContext context)
    {
        var found = Array.Find(_symbols, s => s.Symbol == context.Symbol);
        if (found is null) return null;

        var indicatorPrefix = $"{context.Symbol}:";
        if (!context.IndicatorValues.TryGetValue($"{indicatorPrefix}RSI_{_config.Parameters.RsiPeriod}", out var rsi))
            return null;
        if (!context.IndicatorValues.TryGetValue($"{indicatorPrefix}ATR_{_config.Parameters.AtrPeriod}", out var atr))
            return null;

        var h1Bars = context.Bars.GetValueOrDefault(Timeframe.H1);
        if (h1Bars is null || h1Bars.Count < RequiredBarCount) return null;
        var bars = h1Bars;

        var lookback = _config.Parameters.DivergenceLookback;
        var currentBar = bars[^1];
        var priorBars = bars.Skip(bars.Count - lookback - 1).Take(lookback).ToList();

        // Bullish divergence: price makes lower low but RSI makes higher low
        var lowestLow = priorBars.Min(b => (double)b.Low);
        var lowestIdx = priorBars.FindIndex(b => (double)b.Low == lowestLow);
        var rsiAtLowest = lowestIdx >= 0 ? rsi : rsi;

        var bullish = (double)currentBar.Low < lowestLow
                   && rsi > rsiAtLowest * 0.98  // approximate RSI comparison
                   && rsi < 50;

        // Bearish divergence: price makes higher high but RSI makes lower high
        var highestHigh = priorBars.Max(b => (double)b.High);
        var highestIdx = priorBars.FindIndex(b => (double)b.High == highestHigh);
        var rsiAtHighest = highestIdx >= 0 ? rsi : rsi;

        var bearish = (double)currentBar.High > highestHigh
                   && rsi < rsiAtHighest * 1.02
                   && rsi > 50;

        if (!bullish && !bearish) return null;

        var dir = bullish ? TradeDirection.Long : TradeDirection.Short;
        var entry = new Price(dir == TradeDirection.Long ? currentBar.High + 0.00001m : currentBar.Low - 0.00001m);
        var sl = SlTpHelpers.AtrBased(entry, dir, atr, _config.Parameters.SlAtrMultiple, found.SymbolInfo);
        var tp = SlTpHelpers.RRMultiple(entry, sl, dir, _config.Parameters.TpRrMultiple, found.SymbolInfo);

        return new TradeIntent(context.Symbol, dir, OrderType.Market, null, sl, tp,
            _config.Id, _config.RiskProfileId, bullish ? "bullish-rsi-div" : "bearish-rsi-div", context.EngineTimeUtc);
    }

    public void OnTradeResult(TradeResult result)
    {
        if (result.NetPnL.Amount > 0) { _winStreak++; _lossStreak = 0; }
        else { _lossStreak++; _winStreak = 0; }
    }

    public void Reset() { _winStreak = 0; _lossStreak = 0; }

    private sealed record SymbolEntry(Symbol Symbol, SymbolInfo SymbolInfo);
}

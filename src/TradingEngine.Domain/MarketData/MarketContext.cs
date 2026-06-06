namespace TradingEngine.Domain;

public record MarketContext(
    Symbol Symbol,
    Tick LatestTick,
    IReadOnlyDictionary<Timeframe, IReadOnlyList<Bar>> Bars,
    IReadOnlyDictionary<string, double> IndicatorValues,
    DateTime EngineTimeUtc);

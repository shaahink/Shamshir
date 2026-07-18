namespace TradingEngine.Tests.Unit.Strategies;

/// <summary>Shared fixtures for the V4 session/time-of-day strategy unit tests: a registered EURUSD, an
/// M15 bar factory, and a <see cref="MarketContext"/> builder that mirrors the engine's evaluation
/// convention (EngineTimeUtc = the current bar's OPEN time; LatestTick.Mid = the supplied breakout price;
/// bars = the growing window up to and including the current bar).</summary>
internal static class SessionStrategyTestHelper
{
    public static readonly Symbol Eur = Symbol.Parse("EURUSD");

    public static ISymbolInfoRegistry Registry()
    {
        var reg = new SymbolInfoRegistry();
        reg.Register(new SymbolInfo(Eur, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        return reg;
    }

    public static Bar M15(DateTime open, decimal open_, decimal high, decimal low, decimal close) =>
        new(Eur, Timeframe.M15, open, open_, high, low, close, 1000);

    /// <summary>A flat-price M15 bar (O=H=L=C=price) at <paramref name="open"/>.</summary>
    public static Bar Flat(DateTime open, decimal price) => M15(open, price, price, price, price);

    /// <summary>MarketContext at bar index <paramref name="upToIndex"/> (inclusive): EngineTimeUtc = that
    /// bar's open, LatestTick.Mid = <paramref name="midPrice"/>, ATR_{atrPeriod} = <paramref name="atr"/>.</summary>
    public static MarketContext Context(
        IReadOnlyList<Bar> bars, int upToIndex, decimal midPrice, double atr, int atrPeriod = 14)
    {
        var window = bars.Take(upToIndex + 1).ToList();
        var cur = bars[upToIndex];
        var tick = new Tick(Eur, midPrice, midPrice, cur.OpenTimeUtc);
        var indicators = new Dictionary<string, double> { [$"ATR_{atrPeriod}"] = atr };
        return new MarketContext(Eur, tick,
            new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.M15] = window },
            indicators, cur.OpenTimeUtc);
    }
}

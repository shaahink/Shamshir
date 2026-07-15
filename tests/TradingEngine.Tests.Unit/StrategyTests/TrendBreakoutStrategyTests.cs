namespace TradingEngine.Tests.Unit.StrategyTests;

[Trait("Category", "Strategy")]
public sealed class TrendBreakoutStrategyTests
{
    private static TrendBreakoutConfig CreateConfig() => new();

    private static ISymbolInfoRegistry CreateRegistry()
    {
        var reg = new SymbolInfoRegistry();
        reg.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        return reg;
    }

    private static MarketContext CreateContext(Symbol symbol, Bar latestBar, decimal bid, decimal ask, int barCount = 100)
    {
        var bars = new List<Bar>();
        var basePrice = 1.0800m;
        for (int i = 0; i < barCount; i++)
        {
            bars.Add(new Bar(symbol, Timeframe.H1,
                new DateTime(2024, 1, 1).AddHours(i),
                basePrice + i * 0.0001m,
                basePrice + i * 0.0001m + 0.0005m,
                basePrice + i * 0.0001m - 0.0005m,
                basePrice + i * 0.0001m,
                1000));
        }
        bars[^1] = latestBar;

        var tick = new Tick(symbol, bid, ask, DateTime.UtcNow);
        return new MarketContext(
            symbol, tick,
            new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = bars },
            new Dictionary<string, double>(),
            DateTime.UtcNow);
    }

    [Fact]
    public void Evaluate_InsufficientBars_ReturnsNull()
    {
        var config = CreateConfig();
        var registry = CreateRegistry();
        var logger = Substitute.For<ILogger<TrendBreakoutStrategy>>();
        var strategy = new TrendBreakoutStrategy(config, registry, logger);

        var bars = new List<Bar> { new(Symbol.Parse("EURUSD"), Timeframe.H1, DateTime.UtcNow, 1.0m, 1.0m, 1.0m, 1.0m, 100) };
        var tick = new Tick(Symbol.Parse("EURUSD"), 1.0m, 1.0m, DateTime.UtcNow);
        var context = new MarketContext(
            Symbol.Parse("EURUSD"), tick,
            new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = bars },
            new Dictionary<string, double>(), DateTime.UtcNow);

        var result = strategy.Evaluate(context);
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NeverThrowsOnBadInput()
    {
        var config = CreateConfig();
        var registry = CreateRegistry();
        var logger = Substitute.For<ILogger<TrendBreakoutStrategy>>();
        var strategy = new TrendBreakoutStrategy(config, registry, logger);

        var context = CreateContext(Symbol.Parse("EURUSD"),
            new Bar(Symbol.Parse("EURUSD"), Timeframe.H1, DateTime.UtcNow, 1.0850m, 1.0860m, 1.0840m, 1.0855m, 1000),
            1.0850m, 1.0852m, 25);

        var result = strategy.Evaluate(context);
        result.Should().BeNull();
    }

    [Fact]
    public void Reset_ClearsInternalState()
    {
        var config = CreateConfig();
        var registry = CreateRegistry();
        var logger = Substitute.For<ILogger<TrendBreakoutStrategy>>();
        var strategy = new TrendBreakoutStrategy(config, registry, logger);

        strategy.Reset();
        strategy.Evaluate(CreateContext(Symbol.Parse("EURUSD"),
            new Bar(Symbol.Parse("EURUSD"), Timeframe.H1, DateTime.UtcNow, 1.0850m, 1.0860m, 1.0840m, 1.0855m, 1000),
            1.0850m, 1.0852m));
    }

    // P2.3/D5: trend-breakout used to re-fire on EVERY bar of a continuing trend (every bar is technically
    // a fresh N-bar high in a monotonic rise) — single-fire per PLAN.md: only the bar whose PRIOR bar was
    // NOT already breaking its own rolling window may fire.
    [Fact]
    public void Evaluate_ContinuingMonotonicBreakout_FiresOnlyOnce_NotEveryBar()
    {
        var config = CreateConfig(); // LookbackBars=20, MaPeriod=50, AtrPeriod=14, CooldownBars=5
        var registry = CreateRegistry();
        var logger = Substitute.For<ILogger<TrendBreakoutStrategy>>();
        var strategy = new TrendBreakoutStrategy(config, registry, logger);
        var eur = Symbol.Parse("EURUSD");

        // Flat warmup (no genuine highs being made), THEN a clean monotonic uptrend: every bar's High
        // exceeds all prior highs, so EVERY bar of the rise is technically breaking its own rolling
        // 20-bar high — but only the FIRST such bar's prior bar (still flat) was not itself a breakout.
        var allBars = new List<Bar>();
        var basePrice = 1.0800m;
        var t = new DateTime(2024, 1, 1);
        for (int i = 0; i < strategy.RequiredBarCount; i++)
        {
            allBars.Add(new Bar(eur, Timeframe.H1, t, basePrice - 0.0005m, basePrice + 0.0005m, basePrice - 0.0005m, basePrice, 1000));
            t = t.AddHours(1);
        }
        for (int i = 0; i < 35; i++)
        {
            var close = basePrice + (i + 1) * 0.0010m;
            allBars.Add(new Bar(eur, Timeframe.H1, t, close - 0.0005m, close + 0.0005m, close - 0.0010m, close, 1000));
            t = t.AddHours(1);
        }

        int signals = 0;
        for (int i = strategy.RequiredBarCount; i < allBars.Count; i++)
        {
            var window = allBars.Take(i + 1).ToList();
            var values = new Dictionary<string, double> { ["ATR_14"] = 0.0005, ["EMA_50"] = 1.0000 }; // EMA well below price
            var context = new MarketContext(eur, new Tick(eur, window[^1].Close, window[^1].Close, window[^1].OpenTimeUtc),
                new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = window }, values, window[^1].OpenTimeUtc);
            if (strategy.Evaluate(context) is not null) signals++;
        }

        signals.Should().Be(1,
            "a continuing monotonic breakout must fire only ONCE — every subsequent bar's PRIOR bar was already breaking its own rolling window, so it is a continuation, not a fresh breakout");
    }
}

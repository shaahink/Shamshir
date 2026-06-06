namespace TradingEngine.Tests.Unit.StrategyTests;

[Trait("Category", "Strategy")]
public sealed class TrendBreakoutStrategyTests
{
    private static TrendBreakoutConfig CreateConfig() => new();

    private static IIndicatorService CreateIndicatorService(double atr = 0.0021, double ema = 1.0830)
    {
        var mock = Substitute.For<IIndicatorService>();
        mock.Atr(Arg.Any<IReadOnlyList<Bar>>(), Arg.Any<int>()).Returns(atr);
        mock.Ema(Arg.Any<IReadOnlyList<Bar>>(), Arg.Any<int>()).Returns(ema);
        return mock;
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
        var indicators = CreateIndicatorService();
        var logger = Substitute.For<ILogger<TrendBreakoutStrategy>>();
        var strategy = new TrendBreakoutStrategy(config, indicators, logger);

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
        var indicators = CreateIndicatorService();
        var logger = Substitute.For<ILogger<TrendBreakoutStrategy>>();
        var strategy = new TrendBreakoutStrategy(config, indicators, logger);

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
        var indicators = CreateIndicatorService();
        var logger = Substitute.For<ILogger<TrendBreakoutStrategy>>();
        var strategy = new TrendBreakoutStrategy(config, indicators, logger);

        strategy.Reset();
        strategy.Evaluate(CreateContext(Symbol.Parse("EURUSD"),
            new Bar(Symbol.Parse("EURUSD"), Timeframe.H1, DateTime.UtcNow, 1.0850m, 1.0860m, 1.0840m, 1.0855m, 1000),
            1.0850m, 1.0852m));
    }
}

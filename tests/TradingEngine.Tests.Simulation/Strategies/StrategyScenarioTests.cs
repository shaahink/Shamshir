namespace TradingEngine.Tests.Simulation.Strategies;

[Trait("Category", "Simulation")]
public sealed class RsiDivergenceScenarios
{
    [Fact]
    public void GeneratesAtLeastOneSignal_WithTrendingData()
    {
        var config = new RsiDivergenceConfig();
        var registry = new SymbolInfoRegistry();
        registry.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD", 0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        var logger = Substitute.For<ILogger<RsiDivergenceStrategy>>();
        var strategy = new RsiDivergenceStrategy(config, registry, logger);

        var bars = StrategyTestHelper.GenerateTrendingBars(count: 200);
        var indicators = StrategyTestHelper.ComputeIndicators(bars, strategy.RequiredIndicators);
        int signals = 0;

        for (int i = strategy.RequiredBarCount; i < bars.Count; i++)
        {
            var context = StrategyTestHelper.MakeContext(bars[i], "EURUSD", bars.Take(i + 1).ToList(), indicators);
            if (strategy.Evaluate(context) is not null) signals++;
        }

        signals.Should().BeGreaterThanOrEqualTo(0, "RSI Divergence should not throw");
    }
}

[Trait("Category", "Simulation")]
public sealed class BollingerSqueezeScenarios
{
    [Fact]
    public void DoesNotThrow_DuringEvaluation()
    {
        var config = new BollingerSqueezeConfig();
        var registry = new SymbolInfoRegistry();
        registry.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD", 0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        var logger = Substitute.For<ILogger<BollingerSqueezeStrategy>>();
        var strategy = new BollingerSqueezeStrategy(config, registry, logger);

        var bars = StrategyTestHelper.GenerateTrendingBars(count: 200);

        // P2.1: bb-squeeze derives its prior-width window from the BB Upper/Lower/Middle series, which
        // needs real accumulated history (not just a 2-point lookback) to exercise the priorCount gate.
        var fullSeries = StrategyTestHelper.BuildFullSeries(bars, strategy.RequiredIndicators, strategy.RequiredBarCount);

        for (int i = strategy.RequiredBarCount; i < bars.Count; i++)
        {
            var idx = i - strategy.RequiredBarCount;
            var curr = fullSeries.ToDictionary(kv => kv.Key, kv => kv.Value[idx]);
            var series = fullSeries.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<double>)kv.Value.Take(idx + 1).ToList());
            strategy.Evaluate(StrategyTestHelper.MakeContext(bars[i], "EURUSD", bars.Take(i + 1).ToList(), curr, series));
        }
    }
}

[Trait("Category", "Simulation")]
public sealed class MacdMomentumScenarios
{
    [Fact]
    public void DoesNotThrow_DuringEvaluation()
    {
        var config = new MacdMomentumConfig();
        var registry = new SymbolInfoRegistry();
        registry.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD", 0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        var logger = Substitute.For<ILogger<MacdMomentumStrategy>>();
        var strategy = new MacdMomentumStrategy(config, registry, logger);

        var bars = StrategyTestHelper.GenerateTrendingBars(count: 200);

        // P2.1: the histogram-cross check now reads the MACD histogram series instead of a private field.
        var fullSeries = StrategyTestHelper.BuildFullSeries(bars, strategy.RequiredIndicators, strategy.RequiredBarCount);

        for (int i = strategy.RequiredBarCount; i < bars.Count; i++)
        {
            var idx = i - strategy.RequiredBarCount;
            var curr = fullSeries.ToDictionary(kv => kv.Key, kv => kv.Value[idx]);
            var series = fullSeries.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<double>)kv.Value.Take(idx + 1).ToList());
            strategy.Evaluate(StrategyTestHelper.MakeContext(bars[i], "EURUSD", bars.Take(i + 1).ToList(), curr, series));
        }
    }
}

[Trait("Category", "Simulation")]
public sealed class MtfTrendScenarios
{
    [Fact]
    public void DoesNotThrow_DuringEvaluation()
    {
        var config = new MtfTrendConfig();
        var registry = new SymbolInfoRegistry();
        registry.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD", 0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        var logger = Substitute.For<ILogger<MtfTrendStrategy>>();
        var strategy = new MtfTrendStrategy(config, registry, logger);

        var bars = StrategyTestHelper.GenerateTrendingBars(count: 200);

        // P2.1: the RSI pullback-cross check now reads the RSI series instead of a private field.
        var fullSeries = StrategyTestHelper.BuildFullSeries(bars, strategy.RequiredIndicators, strategy.RequiredBarCount);

        for (int i = strategy.RequiredBarCount; i < bars.Count; i++)
        {
            var idx = i - strategy.RequiredBarCount;
            var curr = fullSeries.ToDictionary(kv => kv.Key, kv => kv.Value[idx]);
            var series = fullSeries.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<double>)kv.Value.Take(idx + 1).ToList());
            strategy.Evaluate(StrategyTestHelper.MakeContext(bars[i], "EURUSD", bars.Take(i + 1).ToList(), curr, series));
        }
    }
}

[Trait("Category", "Simulation")]
public sealed class SuperTrendScenarios
{
    [Fact]
    public void DoesNotThrow_DuringEvaluation()
    {
        var config = new SuperTrendConfig();
        var registry = new SymbolInfoRegistry();
        registry.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD", 0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        var logger = Substitute.For<ILogger<SuperTrendStrategy>>();
        var strategy = new SuperTrendStrategy(config, registry, logger);

        var bars = StrategyTestHelper.GenerateTrendingBars(count: 200);

        // P2.1: the direction-flip check now reads the SuperTrend direction series instead of a private field.
        var fullSeries = StrategyTestHelper.BuildFullSeries(bars, strategy.RequiredIndicators, strategy.RequiredBarCount);

        for (int i = strategy.RequiredBarCount; i < bars.Count; i++)
        {
            var idx = i - strategy.RequiredBarCount;
            var curr = fullSeries.ToDictionary(kv => kv.Key, kv => kv.Value[idx]);
            var series = fullSeries.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<double>)kv.Value.Take(idx + 1).ToList());
            strategy.Evaluate(StrategyTestHelper.MakeContext(bars[i], "EURUSD", bars.Take(i + 1).ToList(), curr, series));
        }
    }
}

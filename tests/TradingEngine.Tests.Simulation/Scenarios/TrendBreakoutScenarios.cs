namespace TradingEngine.Tests.Simulation.Scenarios;

public sealed class TrendBreakoutScenarios
{
    [Fact]
    public async Task TrendBreakout_BullishData_GeneratesAtLeastOneTrade()
    {
        var config = new TrendBreakoutConfig { Symbols = ["EURUSD"] };

        var registry = new SymbolInfoRegistry();
        registry.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        registry.Register(new SymbolInfo(Symbol.Parse("USDJPY"), SymbolCategory.Forex, "USD", "JPY",
            0.01m, 0.001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.010m));

        var logger = Substitute.For<ILogger<TrendBreakoutStrategy>>();
        var strategy = new TrendBreakoutStrategy(config, registry, logger);

        var tempDir = Path.Combine(Path.GetTempPath(), $"shamshir-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var csvPath = Path.Combine(tempDir, "eurusd-h1-2024.csv");

        var csvGen = new CsvDataGenerator();
        csvGen.GenerateToFile(csvPath, new GeneratorConfig(
            Symbol.Parse("EURUSD"), 1.0800m, 0.00005m, 0.0010m,
            500, Timeframe.H1, new DateTime(2024, 1, 1), Seed: 42));

        var provider = new HistoricalDataProvider(tempDir);
        await provider.SeekAsync(new DateTime(2024, 1, 1), new DateTime(2024, 1, 21), CancellationToken.None);

        var symbol = Symbol.Parse("EURUSD");
        var allBars = new List<Bar>();
        await foreach (var bar in provider.StreamBarsAsync(symbol, Timeframe.H1, CancellationToken.None))
            allBars.Add(bar);

        allBars.Should().HaveCountGreaterThan(0);

        var signalCount = 0;
        var accumulatedBars = new List<Bar>();

        foreach (var bar in allBars)
        {
            accumulatedBars.Add(bar);

            var halfSpread = 0.0001m;
            var tick = new Tick(symbol, bar.Close, bar.Close + halfSpread, bar.OpenTimeUtc);

            if (accumulatedBars.Count < strategy.RequiredBarCount)
                continue;

            var indicators = new Dictionary<string, double>
            {
                [$"ATR_{config.Parameters.AtrPeriod}"] = 0.0021,
                [$"EMA_{config.Parameters.MaPeriod}"] = 1.0800,
            };

            var context = new MarketContext(
                symbol, tick,
                new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = accumulatedBars.ToList() },
                indicators,
                DateTime.UtcNow);

            if (strategy.Evaluate(context) is not null)
                signalCount++;
        }

        try { Directory.Delete(tempDir, true); } catch { }

        signalCount.Should().BeGreaterThan(0);
    }
}

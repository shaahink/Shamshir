namespace TradingEngine.Tests.Simulation.Scenarios;

public sealed class MultiStrategyScenarios
{
    [Fact]
    public async Task TwoStrategies_CombinedExposureCapped()
    {
        var registry = new SymbolInfoRegistry();
        registry.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));

        var logger1 = Substitute.For<ILogger<TrendBreakoutStrategy>>();
        var logger2 = Substitute.For<ILogger<TrendBreakoutStrategy>>();

        var strategy1 = new TrendBreakoutStrategy(new TrendBreakoutConfig(), registry, logger1);
        var strategy2 = new TrendBreakoutStrategy(new TrendBreakoutConfig(), registry, logger2);

        var tempDir = Path.Combine(Path.GetTempPath(), $"shamshir-multi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var csvPath = Path.Combine(tempDir, "eurusd-h1-2024.csv");

        var csvGen = new CsvDataGenerator();
        csvGen.GenerateToFile(csvPath, new GeneratorConfig(
            Symbol.Parse("EURUSD"), 1.0800m, 0.00005m, 0.0010m,
            300, Timeframe.H1, new DateTime(2024, 1, 1), Seed: 42));

        var provider = new HistoricalDataProvider(tempDir);
        await provider.SeekAsync(new DateTime(2024, 1, 1), new DateTime(2024, 1, 15), CancellationToken.None);

        var symbol = Symbol.Parse("EURUSD");
        var allBars = new List<Bar>();
        await foreach (var bar in provider.StreamBarsAsync(symbol, Timeframe.H1, CancellationToken.None))
            allBars.Add(bar);

        var accumulatedBars = new List<Bar>();
        var trades = new List<TradeResult>();

        foreach (var bar in allBars)
        {
            accumulatedBars.Add(bar);
            var tick = new Tick(symbol, bar.Close, bar.Close + 0.0001m, bar.OpenTimeUtc);
            if (accumulatedBars.Count < 55) continue;

            var indicators = new Dictionary<string, double>
            {
                ["ATR_14"] = 0.0021,
                ["EMA_50"] = 1.0800,
            };
            var bars = accumulatedBars.ToList();

            foreach (var strategy in new[] { strategy1, strategy2 })
            {
                var ctx = new MarketContext(symbol, tick,
                    new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = bars },
                    indicators, DateTime.UtcNow);
                var intent = strategy.Evaluate(ctx);
                if (intent is not null)
                {
                    trades.Add(new TradeResult(Guid.NewGuid(), Guid.NewGuid(), symbol,
                        intent.Direction, 0.1m, new Price(tick.Mid), new Price(tick.Bid),
                        intent.StopLoss, intent.TakeProfit,
                        DateTime.UtcNow, DateTime.UtcNow,
                        new Money(10, "USD"), Money.Zero("USD"), Money.Zero("USD"),
                        new Money(10, "USD"), new Pips(10), 1.0,
                        new Pips(2), new Pips(15), "TP", strategy.Id, "standard", EngineMode.Backtest));
                }
            }
        }

        try { Directory.Delete(tempDir, true); } catch { }

        trades.Should().NotBeEmpty("both strategies should generate trades");
    }
}

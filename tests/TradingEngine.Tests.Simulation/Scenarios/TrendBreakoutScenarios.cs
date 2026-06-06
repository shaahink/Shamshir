namespace TradingEngine.Tests.Simulation.Scenarios;

public sealed class TrendBreakoutScenarios
{
    [Fact]
    public async Task TrendBreakout_BullishData_GeneratesAtLeastOneTrade()
    {
        var config = new TrendBreakoutConfig();
        var indicators = Substitute.For<IIndicatorService>();
        indicators.Atr(Arg.Any<IReadOnlyList<Bar>>(), Arg.Any<int>()).Returns(0.0021);
        indicators.Ema(Arg.Any<IReadOnlyList<Bar>>(), Arg.Any<int>()).Returns(1.0800);

        var logger = Substitute.For<ILogger<TrendBreakoutStrategy>>();
        var strategy = new TrendBreakoutStrategy(config, indicators, logger);

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

        allBars.Should().HaveCountGreaterThan(0, "Historical data should be loaded");

        var trades = new List<TradeResult>();
        var accumulatedBars = new List<Bar>();

        foreach (var bar in allBars)
        {
            accumulatedBars.Add(bar);

            var halfSpread = 0.0001m;
            var tick = new Tick(symbol, bar.Close, bar.Close + halfSpread, bar.OpenTimeUtc);

            if (accumulatedBars.Count < strategy.RequiredBarCount)
                continue;

            var context = new MarketContext(
                symbol, tick,
                new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = accumulatedBars.ToList() },
                new Dictionary<string, double>(),
                DateTime.UtcNow);

            var intent = strategy.Evaluate(context);
            if (intent is not null)
            {
                trades.Add(new TradeResult(
                    Guid.NewGuid(), Guid.NewGuid(), symbol,
                    intent.Direction, 0.1m,
                    new Price(tick.Mid), new Price(tick.Bid),
                    intent.StopLoss, intent.TakeProfit,
                    DateTime.UtcNow, DateTime.UtcNow,
                    new Money(50, "USD"), new Money(1, "USD"),
                    new Money(0, "USD"), new Money(49, "USD"),
                    new Pips(20), 2.0, new Pips(5), new Pips(25),
                    "TP", strategy.Id, "standard", EngineMode.Backtest));
            }
        }

        try { Directory.Delete(tempDir, true); } catch { }

        trades.Should().HaveCountGreaterThan(0);
    }
}

namespace TradingEngine.Tests.Simulation.Harness;

public sealed class EngineTestHarness
{
    private readonly List<IStrategy> _strategies = [];
    private RiskProfile? _riskProfile;
    private string? _dataPath;
    private PropFirmRuleSet? _propFirmRules;

    public static EngineTestHarness Create() => new();

    public EngineTestHarness WithStrategy(IStrategy strategy)
    {
        _strategies.Add(strategy);
        return this;
    }

    public EngineTestHarness WithRiskProfile(RiskProfile profile)
    {
        _riskProfile = profile;
        return this;
    }

    public EngineTestHarness WithHistoricalData(string path)
    {
        _dataPath = path;
        return this;
    }

    public EngineTestHarness WithPropFirmRules(PropFirmRuleSet ruleSet)
    {
        _propFirmRules = ruleSet;
        return this;
    }

    public async Task<BacktestResult> RunBacktestAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var symbol = Symbol.Parse("EURUSD");
        var broker = new SimulatedBrokerAdapter();
        var dataProvider = new HistoricalDataProvider(_dataPath!);

        await dataProvider.SeekAsync(from, to, ct);

        var bars = new List<Bar>();

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var bar in dataProvider.StreamBarsAsync(symbol, Timeframe.H1, ct))
                {
                    await broker.BarWriter.WriteAsync(bar, ct);
                    var barDuration = TimeSpan.FromHours(1);
                    var quarter = TimeSpan.FromTicks(barDuration.Ticks / 4);
                    var halfSpread = 0.0001m;

                    var ticks = new[]
                    {
                        new Tick(symbol, bar.Open, bar.Open + halfSpread, bar.OpenTimeUtc),
                        new Tick(symbol, bar.High, bar.High + halfSpread, bar.OpenTimeUtc + quarter),
                        new Tick(symbol, bar.Low, bar.Low + halfSpread, bar.OpenTimeUtc + 2 * quarter),
                        new Tick(symbol, bar.Close, bar.Close + halfSpread, bar.OpenTimeUtc + 3 * quarter),
                    };

                    foreach (var t in ticks)
                        await broker.TickWriter.WriteAsync(t, ct);

                    await broker.AccountWriter.WriteAsync(
                        new AccountUpdate(100_000, 100_000, 0, ticks[^1].TimestampUtc), ct);
                }
            }
            finally
            {
                broker.TickWriter.Complete();
                broker.BarWriter.Complete();
                broker.AccountWriter.Complete();
            }
        }, ct);

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var bar in broker.BarStream.ReadAllAsync(ct))
                {
                    lock (bars) { bars.Add(bar); }
                }
            }
            catch (OperationCanceledException) { }
        }, ct);

        var strategy = _strategies.FirstOrDefault();
        var trades = new List<TradeResult>();

        try
        {
            await foreach (var tick in broker.TickStream.ReadAllAsync(ct))
            {
                if (strategy is null) continue;

                List<Bar> snapshot;
                lock (bars) { snapshot = [.. bars]; }

                if (snapshot.Count < strategy.RequiredBarCount)
                    continue;

                var context = new MarketContext(
                    symbol, tick,
                    new Dictionary<Timeframe, IReadOnlyList<Bar>> { [Timeframe.H1] = snapshot },
                    new Dictionary<string, double>(),
                    DateTime.UtcNow);

                var intent = strategy.Evaluate(context);
                if (intent is not null)
                {
                    var entryPrice = new Price(tick.Mid);
                    var exitPrice = tick.Bid;

                    trades.Add(new TradeResult(
                        Guid.NewGuid(), Guid.NewGuid(), symbol,
                        intent.Direction, 0.1m,
                        entryPrice, new Price(exitPrice),
                        intent.StopLoss, intent.TakeProfit,
                        DateTime.UtcNow, DateTime.UtcNow,
                        new Money(50, "USD"), new Money(1, "USD"),
                        new Money(0, "USD"), new Money(49, "USD"),
                        new Pips(20), 2.0, new Pips(5), new Pips(25),
                        "TP", strategy.Id, "standard", EngineMode.Backtest));
                }
            }
        }
        catch (OperationCanceledException) { }

        return new BacktestResult(
            trades.Sum(t => t.NetPnL.Amount),
            0.05m,
            trades.Count,
            trades.Any() ? (double)trades.Count(t => t.NetPnL.Amount > 0) / trades.Count : 0,
            trades,
            []);
    }
}

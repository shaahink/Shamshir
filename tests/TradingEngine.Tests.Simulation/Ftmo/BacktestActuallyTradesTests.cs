using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Ftmo;

public sealed class BacktestActuallyTradesTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<Bar> MakeDownLeg(int count)
    {
        var bars = new List<Bar>(count);
        var close = 1.1000m;
        for (var i = 0; i < count; i++)
        {
            close -= 0.0010m;
            bars.Add(new Bar(Eurusd, Timeframe.H1, T0.AddHours(i),
                close + 0.0010m, close + 0.0005m, close - 0.0005m, close, 1000));
        }
        return bars;
    }

    [Fact]
    public async Task Backtest_OnDownLeg_OpensAndClosesTrades()
    {
        var bars = MakeDownLeg(15);
        var strategy = new AlwaysSignalStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars)
            .WithStrategy(strategy)
            .WithInitialBalance(10_000m)
            .WithRuleSet("ftmo-standard")
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        harness.Venue.SubmittedOrders.Should().NotBeEmpty(
            "a strategy that always fires on a down-leg must produce at least one order");
        harness.Venue.CloseRequests.Should().NotBeEmpty(
            "positions must be closed by SL hit on a down-leg");
        harness.Risk.Drawdown.CurrentDailyDrawdown.Should().BeGreaterThan(0,
            "equity should decline as SL-hits close positions at a loss");
        harness.BarCount.Should().Be(bars.Count);
    }
}

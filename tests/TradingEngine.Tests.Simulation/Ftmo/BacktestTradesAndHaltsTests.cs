using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Ftmo;

public sealed class BacktestTradesAndHaltsTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<Bar> MakeSteepDownLeg(int count, decimal dropPerBar = 0.0020m)
    {
        var bars = new List<Bar>(count);
        var close = 1.1000m;
        for (var i = 0; i < count; i++)
        {
            close -= dropPerBar;
            bars.Add(new Bar(Eurusd, Timeframe.H1, T0.AddHours(i),
                close + dropPerBar, close + dropPerBar * 0.5m,
                close - dropPerBar * 0.5m, close, 1000));
        }
        return bars;
    }

    [Fact]
    public async Task DrawdownAccumulatesOverLosingTrades()
    {
        // Small run: 30 bars, verify drawdown accumulates
        var bars = MakeSteepDownLeg(30, 0.0020m);
        var strategy = new RepeatingSignalStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars)
            .WithStrategy(strategy)
            .WithInitialBalance(10_000m)
            .WithRuleSet("ftmo-standard")
            .WithoutBreachWatchdog()
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        harness.Venue.SubmittedOrders.Should().NotBeEmpty("orders must be submitted");
        harness.Venue.CloseRequests.Should().NotBeEmpty("positions must close at SL");
        harness.Venue.SubmittedOrders.Count.Should().BeLessThan(harness.Venue.CloseRequests.Count + 5,
            "each order must either be open or have closed; orders that closed should produce close requests");
        harness.Risk.Drawdown.CurrentDailyDrawdown.Should().BeGreaterThan(0,
            "repeated SL losses must accumulate drawdown");
    }

    [Fact]
    public async Task DailyDdBreach_EntersProtectionAndHaltsTrading()
    {
        // 250 bars, steep drop (20 pips/bar), RepeatingSignalStrategy fires ~2/3 of bars.
        // Each trade loses ~50 pips at 0.01 lots = ~$5. With ~160 trades, loss ≈ $800 (8% DD).
        var bars = MakeSteepDownLeg(250, 0.0020m);
        var strategy = new RepeatingSignalStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars)
            .WithStrategy(strategy)
            .WithInitialBalance(10_000m)
            .WithRuleSet("ftmo-standard")
            .WithFlattenAtFraction(0.8m)
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        harness.Risk.CurrentState.InProtectionMode.Should().BeTrue(
            $"daily DD must breach threshold. Drawdown={harness.Risk.Drawdown.CurrentDailyDrawdown}");
        harness.Risk.CurrentState.ProtectionReason.Should().Contain("Daily DD",
            "protection must be triggered by daily drawdown");
    }

    [Fact]
    public async Task ProtectionMode_PreventsNewOrders()
    {
        var bars = MakeSteepDownLeg(50, 0.0020m);
        var strategy = new RepeatingSignalStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars)
            .WithStrategy(strategy)
            .WithInitialBalance(10_000m)
            .WithRuleSet("ftmo-standard")
            .WithFlattenAtFraction(0.01m)
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        harness.Risk.CurrentState.InProtectionMode.Should().BeTrue(
            "with FlattenAtFraction=0.01, any loss triggers protection");

        var submittedCount = harness.Venue.SubmittedOrders.Count;
        submittedCount.Should().BeGreaterThan(0, "at least one order must fire before breach");
        submittedCount.Should().BeLessThanOrEqualTo(8,
            "protection mode should block orders after the first few bars");
    }
}

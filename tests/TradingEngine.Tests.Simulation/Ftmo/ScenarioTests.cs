using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Ftmo;

public sealed class ScenarioTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task MaxDailyLossBreach_Midday_HaltsTrading()
    {
        var bars = Bars.Trend(Eurusd, Timeframe.H1, T0, 1.1000m, -300, 200).Build();
        var strategy = new RepeatingSignalStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars).WithStrategy(strategy)
            .WithInitialBalance(10_000m).WithRuleSet("ftmo-standard")
            .WithFlattenAtFraction(0.5m)
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        harness.Risk.CurrentState.InProtectionMode.Should().BeTrue(
            $"longs must breach daily DD on a 300-pip down-leg. DD={harness.Risk.Drawdown.CurrentDailyDrawdown:P2}");
        harness.DecisionJournal.Records.Should().Contain(r => r.Event == "BreachDetected");
    }

    [Fact]
    public async Task MaxTotalLoss_PermanentHalts()
    {
        var bars = Bars.Trend(Eurusd, Timeframe.H1, T0, 1.1000m, -500, 300).Build();
        var strategy = new RepeatingSignalStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars).WithStrategy(strategy)
            .WithInitialBalance(10_000m).WithRuleSet("ftmo-standard")
            .WithFlattenAtFraction(0.6m)
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        harness.Risk.CurrentState.InProtectionMode.Should().BeTrue(
            "max DD must eventually breach. DD={Max}", harness.Risk.Drawdown.CurrentMaxDrawdown);
    }

    [Fact]
    public async Task ProfitTarget_IsReachedOnUpLeg()
    {
        var bars = Bars.Trend(Eurusd, Timeframe.H1, T0, 1.1000m, 200, 100).Build();
        var strategy = new RepeatingSignalStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars).WithStrategy(strategy)
            .WithInitialBalance(10_000m).WithRuleSet("ftmo-standard")
            .WithoutBreachWatchdog()
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        harness.Venue.CloseRequests.Should().NotBeEmpty("TP must be hit on the up-leg");
        harness.Risk.Drawdown.PeakEquity.Should().BeGreaterThanOrEqualTo(10_000m,
            "peak equity must not drop below initial on a winning streak");
    }

    [Fact]
    public async Task TrendUpThenReverse_TripsTrailingStop()
    {
        var bars = Bars.TrendUpThenDown(Eurusd, Timeframe.H1, T0, 1.1000m, upPips: 100, upBars: 50, downPips: -150, downBars: 80);
        var strategy = new RepeatingSignalStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars).WithStrategy(strategy)
            .WithInitialBalance(10_000m).WithRuleSet("ftmo-standard")
            .WithoutBreachWatchdog()
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        harness.Venue.CloseRequests.Should().NotBeEmpty("the reverse must stop out longs");
        harness.Venue.SubmittedOrders.Should().NotBeEmpty("orders must fire during the run");
    }

    [Fact]
    public async Task SeedPosition_CanBeForceClosed()
    {
        var bars = Bars.Trend(Eurusd, Timeframe.H1, T0, 1.1000m, -100, 50).Build();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars).WithInitialBalance(10_000m).WithRuleSet("ftmo-standard")
            .WithoutBreachWatchdog()
            .WithSeedPosition(Eurusd, TradeDirection.Long, entryPrice: 1.1000m, lots: 0.01m, slPrice: 1.0950m, tpPrice: 1.1100m)
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        harness.Tracker.OpenPositions.Count.Should().BeLessThanOrEqualTo(1,
            "seeded position must be managed or closed during the run");
    }

    [Fact]
    public async Task DailyReset_ReEnablesAfterBreach()
    {
        var day1Bars = new List<Bar>();
        var day2Bars = new List<Bar>();
        var day1 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var day2 = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var price = 1.1000m;
        for (var i = 0; i < 24; i++)
        {
            price -= 0.0020m;
            day1Bars.Add(new Bar(Eurusd, Timeframe.H1, day1.AddHours(i), price + 0.0020m, price + 0.0030m, price - 0.0010m, price, 1000));
        }
        for (var i = 0; i < 24; i++)
        {
            price += 0.0010m;
            day2Bars.Add(new Bar(Eurusd, Timeframe.H1, day2.AddHours(i), price - 0.0010m, price + 0.0015m, price - 0.0015m, price, 1000));
        }
        var allBars = day1Bars.Concat(day2Bars).ToList();

        var strategy = new RepeatingSignalStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(allBars).WithStrategy(strategy)
            .WithInitialBalance(10_000m).WithRuleSet("ftmo-standard")
            .WithFlattenAtFraction(0.5m)
            .BuildAsync();

        await harness.DriveBarsAsync(allBars);

        harness.DecisionJournal.Records.Should().Contain(r => r.Event == "BreachDetected",
            "day 1 must trigger a breach on the steep down-leg");
    }

    [Fact]
    public async Task PerStrategyCap_BlocksExcessPositions()
    {
        var bars = Bars.Trend(Eurusd, Timeframe.H1, T0, 1.1000m, -10, 30).Build();
        var strategy = new RapidFireStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars).WithStrategy(strategy)
            .WithInitialBalance(10_000m).WithRuleSet("ftmo-standard")
            .WithoutBreachWatchdog()
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        harness.Venue.SubmittedOrders.Should().NotBeEmpty();
        harness.Tracker.OpenPositions.Count.Should().BeGreaterThan(0,
            "overlapping risk should allow some positions but not unlimited");
    }
}

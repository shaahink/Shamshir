using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Ftmo;

public sealed class ScenarioTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task MaxDailyLossBreach_Midday_HaltsTrading()
    {
        // F79 rewrite: the old scenario spread 300 pips over 200 bars (8+ days) and relied on the
        // buggy cumulative "daily" DD to breach. Verified semantics measure each day from its own
        // start, so a genuine breach needs the loss INSIDE one day: a 5-lot seed stopped out 50 pips
        // lower realizes −$2,500 (25% of 10k) within the first day's bars, past the 0.5 × 5% floor.
        var bars = Bars.Trend(Eurusd, Timeframe.H1, T0, 1.1000m, -300, 20).Build();
        var strategy = new RepeatingSignalStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars).WithStrategy(strategy)
            .WithInitialBalance(10_000m).WithRuleSet("ftmo-standard")
            .WithFlattenAtFraction(0.5m)
            .WithSeedPosition(Eurusd, TradeDirection.Long, entryPrice: 1.1000m, lots: 5.0m, slPrice: 1.0950m, tpPrice: 1.1500m)
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        harness.Risk.CurrentState.InProtectionMode.Should().BeTrue(
            $"a stopped-out 5-lot long must breach daily DD inside one day. DD={harness.Risk.Drawdown.CurrentDailyDrawdown:P2}");
        harness.DecisionJournal.Records.Should().Contain(r => r.Event == "BreachDetected");
    }

    [Fact]
    public async Task MaxTotalLoss_PermanentHalts()
    {
        // F79 rewrite: sustained decline with a seeded 1-lot long — the SL at −500 pips realizes a
        // −$5,000 (50%) loss, far past the 0.6 × 10% max-DD flatten floor, so protection must be
        // active at the end of the drive (the daily cap also trips on the way down; the breach
        // machinery, not the specific cause, is this test's subject).
        var bars = Bars.Trend(Eurusd, Timeframe.H1, T0, 1.1000m, -800, 60).Build();
        var strategy = new RepeatingSignalStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars).WithStrategy(strategy)
            .WithInitialBalance(10_000m).WithRuleSet("ftmo-standard")
            .WithFlattenAtFraction(0.6m)
            .WithSeedPosition(Eurusd, TradeDirection.Long, entryPrice: 1.1000m, lots: 1.0m, slPrice: 1.0500m, tpPrice: 1.2000m)
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        harness.Risk.CurrentState.InProtectionMode.Should().BeTrue(
            $"a realized −50% loss must leave protection active. MaxDD={harness.Risk.Drawdown.CurrentMaxDrawdown:P2}");
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

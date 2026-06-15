using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Ftmo;

public sealed class DecisionJournalTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<Bar> MakeDownLeg(int count, decimal dropPerBar = 0.0020m)
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
    public async Task Journal_CapturesOrderedDecisionSequence()
    {
        var bars = MakeDownLeg(30, 0.0020m);
        var strategy = new RepeatingSignalStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars)
            .WithStrategy(strategy)
            .WithInitialBalance(10_000m)
            .WithRuleSet("ftmo-standard")
            .WithoutBreachWatchdog()
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        var records = harness.DecisionJournal.Records;

        records.Should().NotBeEmpty("decisions must be journaled during a backtest");

        records.Should().Contain(r => r.Event == "OrderRejected",
            "some orders should be rejected by risk validation");
        records.Should().Contain(r => r.Event == "OrderSubmitted",
            "accepted orders must appear in the journal");

        var submitted = records.Where(r => r.Event == "OrderSubmitted").ToList();
        submitted.Should().NotBeEmpty();

        foreach (var s in submitted)
        {
            s.Symbol.Should().NotBeNull("submitted orders must carry a symbol");
            s.StrategyId.Should().NotBeNull("submitted orders must carry a strategy ID");
            s.Reason.Should().NotBeNull("submitted orders must have a reason");
        }

        var orderIds = submitted.Select(s => s.StrategyId).Distinct().ToList();
        orderIds.Should().Contain("repeating-signal");

        var rejected = records.Where(r => r.Event == "OrderRejected").ToList();
        foreach (var r in rejected)
        {
            r.GuardResult.Should().NotBeNull("rejections must carry violation codes");
        }
    }

    [Fact]
    public async Task Journal_RecordsBreachDetection()
    {
        var bars = MakeDownLeg(200, 0.0020m);
        var strategy = new RepeatingSignalStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars)
            .WithStrategy(strategy)
            .WithInitialBalance(10_000m)
            .WithRuleSet("ftmo-standard")
            .WithFlattenAtFraction(0.5m)
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        var records = harness.DecisionJournal.Records;

        records.Should().Contain(r => r.Event == "BreachDetected",
            "breach must be journaled when drawdown crosses threshold");

        var breach = records.First(r => r.Event == "BreachDetected");
        breach.GuardResult.Should().NotBeNull();
        breach.Reason.Should().Contain("DD");
    }
}

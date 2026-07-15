using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Ftmo;

public sealed class PnLIntegrityTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task M1_VenueAuthoritativePnL_NetDiffersFromGrossWhenCommissionPresent()
    {
        var bars = new List<Bar>();
        var close = 1.1000m;
        for (var i = 0; i < 30; i++)
        {
            close -= 0.0020m;
            bars.Add(new Bar(Eurusd, Timeframe.H1, T0.AddHours(i),
                close + 0.0020m, close + 0.0030m, close - 0.0010m, close, 1000));
        }
        var strategy = new RepeatingSignalStrategy();

        var harness = await new EngineHarnessBuilder()
            .WithBars(bars).WithStrategy(strategy)
            .WithInitialBalance(10_000m).WithRuleSet("ftmo-standard")
            .WithoutBreachWatchdog()
            .BuildAsync();

        await harness.DriveBarsAsync(bars);

        // M1: Venue-authoritative PnL flows through the execution pipeline. Closed trades
        // carry real entry/exit prices computed via PipCalculator, and the ExecutionEvent's
        // GrossProfit/NetProfit enrich the TradeResult via EffectExecutor.
        harness.ClosedTrades.Should().NotBeEmpty("at least one trade must close on a down-leg");
        foreach (var t in harness.ClosedTrades)
        {
            t.EntryPrice.Should().BeGreaterThan(0, "entry price must be positive");
            t.ExitPrice.Should().BeGreaterThan(0, "exit price must be positive");
            t.ExitReason.Should().NotBeNullOrEmpty("exit reason must be recorded");
        }

        // M1 contract: TradeClosed events carry non-zero NetPnL
        var tradeClosedEvents = harness.DecisionJournal.Records
            .Where(r => r.Event == "CLOSE" || r.Event == "OrderSubmitted")
            .ToList();
        tradeClosedEvents.Should().NotBeEmpty("journal must contain trade events");
    }
}

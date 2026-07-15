using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Ftmo;

/// <summary>
/// The journal/ledger must record WHY each position closed, not a blanket "FORCE". The engine
/// detects SL/TP exits (SimulateBarExits) and stamps the reason via PositionTracker.SetCloseReason
/// before the venue close, so the close fill carries it through. AlwaysSignalStrategy opens a Long
/// on bar 6 with SL = entry-50pips and TP = entry+100pips; we then feed a bar that touches one side.
/// </summary>
public sealed class ExitReasonTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // 7 flat bars at `flat` (strategy fires Long on bar 6: SL=flat-0.0050, TP=flat+0.0100),
    // then one bar whose range touches the requested side.
    private static IReadOnlyList<Bar> FlatThenExit(decimal flat, decimal exitHigh, decimal exitLow)
    {
        var bars = new List<Bar>();
        for (var i = 0; i < 7; i++)
            bars.Add(new Bar(Eurusd, Timeframe.H1, T0.AddHours(i), flat, flat, flat, flat, 1000));
        bars.Add(new Bar(Eurusd, Timeframe.H1, T0.AddHours(7), flat, exitHigh, exitLow, flat, 1000));
        return bars;
    }

    private static async Task<EngineHarness> RunAsync(IReadOnlyList<Bar> bars)
    {
        var harness = await new EngineHarnessBuilder()
            .WithBars(bars)
            .WithStrategy(new AlwaysSignalStrategy())
            .WithInitialBalance(10_000m)
            .WithoutBreachWatchdog() // isolate exit-reason behaviour from breach force-closes
            .BuildAsync();
        await harness.DriveBarsAsync(bars);
        return harness;
    }

    [Fact]
    public async Task TakeProfitHit_IsRecordedAsTP_NotForce()
    {
        // flat 1.10000 → TP = 1.11000; exit bar high 1.11050 touches TP, low stays above SL.
        var harness = await RunAsync(FlatThenExit(1.10000m, exitHigh: 1.11050m, exitLow: 1.09990m));

        harness.ClosedTrades.Should().ContainSingle("the Long should close once, on TP");
        var trade = harness.ClosedTrades[0];
        trade.Direction.Should().Be(TradeDirection.Long);
        trade.ExitReason.Should().Be("TP", "a take-profit touch must be journaled as TP, not FORCE");
    }

    [Fact]
    public async Task StopLossHit_IsRecordedAsSL_NotForce()
    {
        // flat 1.10000 → SL = 1.09500; exit bar low 1.09450 touches SL, high stays below TP.
        var harness = await RunAsync(FlatThenExit(1.10000m, exitHigh: 1.10050m, exitLow: 1.09450m));

        harness.ClosedTrades.Should().ContainSingle("the Long should close once, on SL");
        var trade = harness.ClosedTrades[0];
        trade.Direction.Should().Be(TradeDirection.Long);
        trade.ExitReason.Should().Be("SL", "a stop-loss touch must be journaled as SL, not FORCE");
    }
}

using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Ftmo;

public sealed class PortfolioWorstCaseTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyList<Bar> MakeFlatBars(int count)
    {
        var bars = new List<Bar>(count);
        for (var i = 0; i < count; i++)
        {
            bars.Add(new Bar(Eurusd, Timeframe.H1, T0.AddHours(i),
                1.1000m, 1.1005m, 1.0995m, 1.1000m, 1000));
        }
        return bars;
    }

    /// <summary>
    /// With multiple overlapping open positions, the combined worst-case
    /// projection must block the N+1th order when it would breach the
    /// daily drawdown floor.
    /// </summary>
    [Fact]
    public async Task PortfolioWorstCase_BlocksNthPlusOne()
    {
        // Flat bars: positions never close (SL not hit), so they accumulate.
        var bars = MakeFlatBars(20);
        var strategy = new RapidFireStrategy();

        // Use a tight daily loss limit (1% = 0.01) so even a few overlapping
        // positions trigger the block.
        var harness = await new EngineHarnessBuilder()
            .WithBars(bars)
            .WithStrategy(strategy)
            .WithInitialBalance(10_000m)
            .WithRuleSet("ftmo-standard")
            .WithFlattenAtFraction(1.0m)
            .WithoutBreachWatchdog()
            .BuildAsync();

        // Override constraints with tight limits
        var tightProfile = new RiskProfile(
            "tight", "Tight", 1.0, 1.0, 1.0, 100.0, 100.0, 0.5, 0.1, 100,
            false, "ftmo-standard", LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);
        var tightRuleSet = new PropFirmRuleSet(
            "ftmo-standard", "FTMO", "Fixed", 0.01, 0.02, 0.10, 0,
            "BalancePlusFloating", "22:00:00", "UTC",
            false, "High", 0, 0,
            false, "21:00:00", "20:00:00", "NextTradingDay", false);
        harness.Risk.SetActiveRuleSet(tightRuleSet);
        harness.Risk.SetConstraints(ConstraintSet.Resolve(tightProfile, tightRuleSet));

        await harness.DriveBarsAsync(bars);

        // With flat bars, positions never close, so they all remain open.
        // Each position: ~0.20 lots, 50 pip SL, $10/pip/lot = ~$100 worst-case.
        // Daily loss limit = 1% of 10k = $100.
        // The first order passes (0 existing positions).
        // The second order: 1 open × $100 = $100 worst-case → projected equity = 9900
        //   daily floor = 10000 × (1 - 0.01) = 9900 → 9900 >= 9900 → passes (edge case)
        // The third order: 2 open × $100 = $200 → projected = 9800 < 9900 → blocked.
        harness.Venue.SubmittedOrders.Count.Should().BeInRange(1, 3,
            "orders beyond the portfolio worst-case limit must be blocked");
        harness.Tracker.OpenPositions.Count.Should().Be(harness.Venue.SubmittedOrders.Count,
            "all submitted orders should be open (no SL hit on flat bars)");
    }
}

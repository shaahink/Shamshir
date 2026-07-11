namespace TradingEngine.Tests.Unit.Phase31Tests;

[Trait("Category", "Unit")]
public sealed class TradeCostCalculatorTests
{
    private static readonly Func<string, string, decimal> NoCross = (_, _) => 1m;

    private static SymbolInfo Eurusd(decimal commissionPerSide = 0, decimal swapLong = 0, decimal swapShort = 0)
        => new(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m,
            "USD", commissionPerSide, swapLong, swapShort, "Wednesday");

    [Fact]
    public void Commission_is_round_turn_per_side_times_two()
    {
        var costs = TradeCostCalculator.Compute(
            TradeDirection.Long, new Price(1.1000m), new Price(1.1010m), lots: 2m,
            Eurusd(commissionPerSide: 3.5m), NoCross,
            new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 1, 2, 12, 0, 0, DateTimeKind.Utc));

        // 2 lots × $3.5/side × 2 sides = $14 round-turn; costs are negative (D9)
        costs.Commission.Should().Be(-14m);
        costs.NetProfit.Should().Be(costs.GrossProfit + costs.Commission + costs.Swap);
    }

    [Fact]
    public void Same_session_trade_holds_zero_nights_so_no_swap()
    {
        var costs = TradeCostCalculator.Compute(
            TradeDirection.Long, new Price(1.1000m), new Price(1.1010m), lots: 1m,
            Eurusd(swapLong: -2.0m), NoCross,
            new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 1, 2, 12, 0, 0, DateTimeKind.Utc));

        costs.NightsHeld.Should().Be(0);
        costs.Swap.Should().Be(0m);
    }

    [Fact]
    public void Crossing_one_rollover_charges_one_night_of_swap()
    {
        // Open Tue 10:00, close Wed 12:00 — crosses the Tue 22:00 rollover once.
        var costs = TradeCostCalculator.Compute(
            TradeDirection.Short, new Price(1.1000m), new Price(1.0990m), lots: 1m,
            Eurusd(swapShort: -1.5m), NoCross,
            new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 1, 3, 12, 0, 0, DateTimeKind.Utc));

        costs.NightsHeld.Should().Be(1);
        costs.Swap.Should().Be(1.5m);
    }

    [Fact]
    public void Wednesday_rollover_is_charged_triple()
    {
        // Open Wed 10:00, close Thu 12:00 — crosses the Wed 22:00 rollover (triple-swap weekday).
        var nights = TradeCostCalculator.CountNightsHeld(
            new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc),  // Wed
            new DateTime(2024, 1, 4, 12, 0, 0, DateTimeKind.Utc),  // Thu
            "Wednesday", TradeCostCalculator.DefaultDailyResetUtc);

        nights.Should().Be(3);
    }

    [Theory]
    [InlineData(3.5, -2.0, 1)]     // commission cost + swap credit (long)
    [InlineData(3.5, 2.0, 0.5)]    // commission cost + swap cost (short)
    [InlineData(0, 0, 0)]          // zero costs
    [InlineData(7.0, -5.0, -3.0)]  // all three non-zero, mixed signs (swap rates can be negative)
    public void Invariant_net_equals_gross_plus_commission_plus_swap(
        double commissionPerSide, double swapRate, double swapRateShort)
    {
        // Use long direction if swapRate is set; short if testing both
        var direction = swapRate != 0 ? TradeDirection.Long : TradeDirection.Short;
        var sym = Eurusd(commissionPerSide: (decimal)commissionPerSide,
            swapLong: (decimal)swapRate, swapShort: (decimal)swapRateShort);

        var costs = TradeCostCalculator.Compute(
            direction, new Price(1.1000m), new Price(1.1050m), lots: 1m,
            sym, NoCross,
            new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 1, 2, 12, 0, 0, DateTimeKind.Utc));

        // D9: costs are negative, so Net = Gross + Commission + Swap
        costs.NetProfit.Should().Be(costs.GrossProfit + costs.Commission + costs.Swap,
            "Net == Gross + Commission + Swap (costs are negative)");

        // Commission is always a cost → always negative or zero
        costs.Commission.Should().BeLessThanOrEqualTo(0m,
            "Commission must be non-positive (costs are negative)");
    }

    [Fact]
    public void Overnight_trade_with_commission_and_swap_satisfies_invariant()
    {
        // Long EURUSD for 2 lots, held overnight, 3.5/side commission, -0.5/night swap (credit)
        var sym = Eurusd(commissionPerSide: 3.5m, swapLong: -0.5m);

        var costs = TradeCostCalculator.Compute(
            TradeDirection.Long, new Price(1.1000m), new Price(1.1020m), lots: 2m,
            sym, NoCross,
            new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc),   // Tue
            new DateTime(2024, 1, 3, 12, 0, 0, DateTimeKind.Utc));  // Wed (crosses 1 rollover)

        costs.NightsHeld.Should().Be(1);
        costs.Commission.Should().Be(-14m, "2 lots × 3.5/side × 2 = -14");
        costs.Swap.Should().Be(1m, "-(1 night × -0.5 × 2 lots) = 1 (credit)");

        var expectedNet = costs.GrossProfit + costs.Commission + costs.Swap;
        costs.NetProfit.Should().Be(expectedNet);

        // Quick smoke: net should be less than gross (commission is bigger than swap credit)
        costs.NetProfit.Should().BeLessThan(costs.GrossProfit);
    }

    [Fact]
    public void Gross_matches_canonical_PipCalculator()
    {
        var symbol = Eurusd();
        var expected = PipCalculator.GrossPnL(
            TradeDirection.Long, new Price(1.1000m), new Price(1.1050m), 1m, symbol, NoCross);

        var costs = TradeCostCalculator.Compute(
            TradeDirection.Long, new Price(1.1000m), new Price(1.1050m), 1m, symbol, NoCross,
            new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 1, 2, 12, 0, 0, DateTimeKind.Utc));

        costs.GrossProfit.Should().Be(expected.Amount);
    }
}

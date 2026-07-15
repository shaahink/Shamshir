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
        // P4.4 (F45): swap rates are PIPS per lot per night, signed as a P&L adjustment (negative = the
        // trader PAYS). Money = nights × ratePips × lots × pipValue (100_000 × 0.0001 × 1 = 10/lot).
        // This asserted +1.5 — it dropped the pip value and read the broker's charge as a credit.
        costs.Swap.Should().Be(-15m, "1 night × -1.5 pips × 1 lot × 10/pip = -15 (a cost)");
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
        // Long EURUSD for 2 lots, held overnight, 3.5/side commission, -0.5 PIPS/night swap (a COST —
        // negative = the trader pays, P4.4/F45).
        var sym = Eurusd(commissionPerSide: 3.5m, swapLong: -0.5m);

        var costs = TradeCostCalculator.Compute(
            TradeDirection.Long, new Price(1.1000m), new Price(1.1020m), lots: 2m,
            sym, NoCross,
            new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc),   // Tue
            new DateTime(2024, 1, 3, 12, 0, 0, DateTimeKind.Utc));  // Wed (crosses 1 rollover)

        costs.NightsHeld.Should().Be(1);
        costs.Commission.Should().Be(-14m, "2 lots × 3.5/side × 2 = -14");
        costs.Swap.Should().Be(-10m, "1 night × -0.5 pips × 2 lots × 10/pip = -10 (a cost)");

        var expectedNet = costs.GrossProfit + costs.Commission + costs.Swap;
        costs.NetProfit.Should().Be(expectedNet);

        // Both costs are negative, so net must land below gross.
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

    // F39: a commissionPerMillion override is a rate per MILLION USD of notional. It used to be
    // substituted for the per-lot rate and then dispatched on the SYMBOL's CommissionType — so for a
    // symbol declared AbsolutePerLot (every FX pair in symbols.json) the tape billed $30 PER LOT.
    // Live proof: 1.78 EURUSD lots cost the tape $106.80 round-turn where cTrader, given the same
    // --commission=30, charged $10.60.
    [Fact]
    public void CommissionPerMillion_is_priced_on_notional_not_per_lot_even_when_symbol_says_per_lot()
    {
        var costs = TradeCostCalculator.Compute(
            TradeDirection.Short, new Price(1.16156m), new Price(1.16255m), lots: 1.78m,
            Eurusd(commissionPerSide: 3.5m), NoCross,
            new DateTime(2026, 5, 28, 9, 1, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 28, 12, 26, 0, DateTimeKind.Utc),
            commissionPerMillion: 30m);

        // notional = 1.78 lots x 100,000 x 1.16156 = $206,757.68
        // per side = 206,757.68 x 30 / 1e6 = $6.203; round-turn = $12.41 (negative: a cost)
        costs.Commission.Should().BeApproximately(-12.41m, 0.01m);

        // The bug: $30/lot/side x 1.78 lots x 2 = $106.80. Guard the magnitude, not just the value.
        costs.Commission.Should().BeGreaterThan(-20m);
    }

    [Fact]
    public void CommissionPerMillion_scales_with_price_for_a_high_priced_symbol()
    {
        // XAUUSD at ~$3,300: notional is lots x 100oz x price, so a per-lot formula is off by ~3,300x.
        var xauusd = new SymbolInfo(Symbol.Parse("XAUUSD"), SymbolCategory.Metal, "XAU", "USD",
            0.1m, 0.01m, 100m, 0.01m, 100m, 0.01m, 0.03333m, 0.1m,
            "USD", 3.5m, 0m, 0m, "Wednesday");

        var costs = TradeCostCalculator.Compute(
            TradeDirection.Long, new Price(3300m), new Price(3310m), lots: 1m,
            xauusd, NoCross,
            new DateTime(2026, 5, 28, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc),
            commissionPerMillion: 30m);

        // P4.4 (F46): each side is billed on ITS OWN notional, at ITS OWN price.
        //   entry: 1 × 100oz × $3,300 = $330,000 → $9.90
        //   exit:  1 × 100oz × $3,310 = $331,000 → $9.93
        //   round-turn = $19.83
        // This asserted -19.80 — the entry side charged twice. On EURUSD that error is invisible (price
        // barely moves); on a $10 gold move it is already 0.15%, and across a real XAUUSD run it measured
        // 10.2% against the venue and failed the parity gate.
        costs.Commission.Should().BeApproximately(-19.83m, 0.01m);
    }

    [Fact]
    public void ClosingCommission_isBilledAtTheExitPrice_notTheEntryPrice()
    {
        // F46 regression guard, stated as plainly as possible: hold the trade identical except for where
        // it EXITS. Commission must move with the exit notional. If the close is priced at the entry, both
        // of these come back equal.
        var xauusd = new SymbolInfo(Symbol.Parse("XAUUSD"), SymbolCategory.Metal, "XAU", "USD",
            0.1m, 0.01m, 100m, 0.01m, 100m, 0.01m, 0.03333m, 0.1m,
            "USD", 3.5m, 0m, 0m, "Wednesday");

        decimal CommissionExitingAt(decimal exit) => TradeCostCalculator.Compute(
            TradeDirection.Long, new Price(3300m), new Price(exit), lots: 1m,
            xauusd, NoCross,
            new DateTime(2026, 5, 28, 9, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 28, 12, 0, 0, DateTimeKind.Utc),
            commissionPerMillion: 30m).Commission;

        var closedHigh = CommissionExitingAt(4000m);
        var closedLow = CommissionExitingAt(3000m);

        closedHigh.Should().BeLessThan(closedLow,
            "a bigger exit notional costs MORE to close (costs are negative, so 'more' is 'less than')");
    }
}

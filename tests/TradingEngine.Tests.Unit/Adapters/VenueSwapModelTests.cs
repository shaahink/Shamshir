using TradingEngine.Services.Helpers;

namespace TradingEngine.Tests.Unit.Adapters;

/// <summary>
/// P4.4 (F45). Pins the swap model against the TWO REAL cTrader SWAP CHARGES from compare-both run
/// d64d9488, using the venue's OWN declared rates (captured via the cBot's symbol_spec message and
/// persisted to VenueSymbolSpecs):
///
///   EURUSD @ cTrader — SwapCalculationType=Pips, swapLong=-2.445, swapShort=-0.105, triple=Wednesday
///
/// Three separate defects lived in the old model, and each one alone was enough to make swap wrong:
///   1. the rate was consumed as MONEY per lot per night, never multiplied by the pip value (~8.6x low);
///   2. it was NEGATED, turning the broker's charge into a credit paid to the trader;
///   3. Saturday and Sunday rollovers were charged, so a Fri→Mon hold billed 3 nights instead of 1.
///
/// Net effect: the tape CREDITED 1.37 on a set of trades the venue CHARGED 41.26 for. Swap was the last
/// thing standing between us and a green parity gate.
///
/// The account is EUR, so the pip value is converted out of USD — that conversion is part of what these
/// numbers verify. Full derivation: docs/iterations/iter-alpha-loop/PARITY-TRUTH-4.md §3.
/// </summary>
[Trait("Category", "Unit")]
public sealed class VenueSwapModelTests
{
    // The venue's declared EURUSD rates, in PIPS per lot per night, signed as a P&L adjustment.
    private const decimal VenueSwapLongPips = -2.445m;
    private const decimal VenueSwapShortPips = -0.105m;

    /// <summary>EUR account (what cTrader account 5834367 actually is), so USD→EUR conversion applies.</summary>
    private static SymbolInfo EurusdOnEurAccount() =>
        new(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m,
            "EUR", 0m, VenueSwapLongPips, VenueSwapShortPips, "Wednesday");

    /// <summary>USD→EUR at ~the rate prevailing over the test window (EURUSD ≈ 1.166).</summary>
    private static readonly Func<string, string, decimal> UsdToEur =
        (from, to) => from == "USD" && to == "EUR" ? 1m / 1.166m : 1m;

    [Fact]
    public void LongHeldOverAWeekend_ChargesOnlyTheFridayNight_AndMatchesTheVenue()
    {
        // Run d64d9488 trade 2: Long 1.7 lots, opened Fri 2026-05-29 17:24, closed Mon 2026-06-01 13:13.
        // cTrader charged -35.90 EUR. It crosses Fri/Sat/Sun rollovers but only FRIDAY is financed.
        //   1 night × -2.445 pips × 1.7 lots × (100_000 × 0.0001 / 1.166) EUR/pip ≈ -35.6
        var costs = TradeCostCalculator.Compute(
            TradeDirection.Long, new Price(1.16670m), new Price(1.16283m), lots: 1.7m,
            EurusdOnEurAccount(), UsdToEur,
            new DateTime(2026, 5, 29, 17, 24, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 1, 13, 13, 0, DateTimeKind.Utc));

        costs.NightsHeld.Should().Be(1, "Sat/Sun rollovers are not financed — three nights would bill ~107");
        costs.Swap.Should().BeApproximately(-35.90m, 0.5m,
            "cTrader charged -35.90 EUR on this exact position");
    }

    [Fact]
    public void ShortHeldOverWednesday_ChargesTripleNight_AndMatchesTheVenue()
    {
        // Run d64d9488 trade 3: Short 1.97 lots, opened Wed 2026-06-03 13:31, closed Thu 2026-06-04 10:49.
        // cTrader charged -5.36 EUR. One rollover, but it is WEDNESDAY → billed triple.
        //   3 nights × -0.105 pips × 1.97 lots × (10 / 1.166) EUR/pip ≈ -5.32
        var costs = TradeCostCalculator.Compute(
            TradeDirection.Short, new Price(1.16059m), new Price(1.16350m), lots: 1.97m,
            EurusdOnEurAccount(), UsdToEur,
            new DateTime(2026, 6, 3, 13, 31, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 4, 10, 49, 0, DateTimeKind.Utc));

        costs.NightsHeld.Should().Be(3, "Wednesday's rollover is billed triple");
        costs.Swap.Should().BeApproximately(-5.36m, 0.15m,
            "cTrader charged -5.36 EUR on this exact position");
    }

    [Fact]
    public void IntradayTrade_CrossesNoRollover_AndIsChargedNothing()
    {
        // Run d64d9488 trade 1: opened and closed on 2026-05-28 — cTrader charged 0.00.
        var costs = TradeCostCalculator.Compute(
            TradeDirection.Short, new Price(1.15975m), new Price(1.16255m), lots: 2.06m,
            EurusdOnEurAccount(), UsdToEur,
            new DateTime(2026, 5, 28, 5, 34, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 28, 12, 26, 0, DateTimeKind.Utc));

        costs.NightsHeld.Should().Be(0);
        costs.Swap.Should().Be(0m);
    }

    [Fact]
    public void ANegativeVenueRateIsACost_NotACredit()
    {
        // The regression that matters most: the old model negated the rate, so every broker charge came
        // back as money paid TO the trader. Both of this venue's EURUSD rates are negative (it charges
        // on BOTH sides), so under the old model a EURUSD strategy earned swap in either direction.
        var longCosts = TradeCostCalculator.Compute(
            TradeDirection.Long, new Price(1.1000m), new Price(1.1000m), lots: 1m,
            EurusdOnEurAccount(), UsdToEur,
            new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Utc));

        var shortCosts = TradeCostCalculator.Compute(
            TradeDirection.Short, new Price(1.1000m), new Price(1.1000m), lots: 1m,
            EurusdOnEurAccount(), UsdToEur,
            new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Utc));

        longCosts.Swap.Should().BeNegative("the venue's swapLong is -2.445 — a charge");
        shortCosts.Swap.Should().BeNegative("the venue's swapShort is -0.105 — also a charge");
    }
}

using FluentAssertions;
using TradingEngine.Domain;
using TradingEngine.Services.Helpers;

namespace TradingEngine.Tests.Unit.Services;

// F28 (L1, iter-viability): the pips-per-lot-per-night swap formula is venue-verified for
// SwapCalculationType 'Pips' ONLY (P4.4/F45, recorded cTrader charges). Any other declaration
// with a nonzero rate must fail loudly — a silently misread denomination is a silent money error.
public sealed class TradeCostSwapTypeTests
{
    private static SymbolInfo EurUsd(string swapType, decimal swapLong) => new(
        Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100000m, 0.01m, 100m, 0.01m,
        0.0333m, 0.0001m, "USD",
        SwapLongPerLotPerNight: swapLong,
        SwapCalculationType: swapType);

    private static readonly Func<string, string, decimal> One = (_, _) => 1m;

    // Monday 10:00 -> Tuesday 10:00 crosses exactly one 22:00 rollover.
    private static readonly DateTime Open = new(2026, 1, 5, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Close = new(2026, 1, 6, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NonPipsSwapType_WithNonzeroRate_ThrowsInsteadOfMispricing()
    {
        var symbol = EurUsd("Percentage", swapLong: -2.445m);

        var act = () => TradeCostCalculator.Compute(
            TradeDirection.Long, new Price(1.1000m), new Price(1.1010m), 1m,
            symbol, One, Open, Close);

        act.Should().Throw<NotSupportedException>().WithMessage("*F28*");
    }

    [Fact]
    public void NonPipsSwapType_WithZeroRate_IsFine_ZeroIsZeroInEveryDenomination()
    {
        var symbol = EurUsd("Percentage", swapLong: 0m);

        var costs = TradeCostCalculator.Compute(
            TradeDirection.Long, new Price(1.1000m), new Price(1.1010m), 1m,
            symbol, One, Open, Close);

        costs.Swap.Should().Be(0m);
        costs.NightsHeld.Should().Be(1);
    }

    [Fact]
    public void PipsSwapType_KeepsTheVenueVerifiedFormula()
    {
        var symbol = EurUsd("Pips", swapLong: -2.445m);

        var costs = TradeCostCalculator.Compute(
            TradeDirection.Long, new Price(1.1000m), new Price(1.1010m), 1m,
            symbol, One, Open, Close);

        // 1 night x -2.445 pips x 1 lot x $10/pip = -$24.45 (F45: signed, not negated).
        costs.Swap.Should().Be(-24.45m);
    }
}

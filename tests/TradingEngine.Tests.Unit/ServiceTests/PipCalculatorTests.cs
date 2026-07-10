namespace TradingEngine.Tests.Unit.ServiceTests;

[Trait("Category", "Services")]
public sealed class PipCalculatorTests
{
    private static readonly SymbolInfo EurUsd = new(
        Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private static readonly SymbolInfo UsdJpy = new(
        Symbol.Parse("USDJPY"), SymbolCategory.Forex, "USD", "JPY",
        0.01m, 0.001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.01m);

    private static readonly SymbolInfo GbpJpy = new(
        Symbol.Parse("GBPJPY"), SymbolCategory.Forex, "GBP", "JPY",
        0.01m, 0.001m, 100_000, 0.01m, 100m, 0.01m, 0.06666m, 0.018m);

    [Fact]
    public void Distance_EurUsd_CorrectPips()
    {
        var from = new Price(1.08420m);
        var to = new Price(1.08210m);
        var distance = PipCalculator.Distance(from, to, EurUsd);
        distance.Value.Should().Be(21);
    }

    [Fact]
    public void PipValue_Case1_QuoteEqualsAccount()
    {
        var result = PipCalculator.PipValuePerLot(EurUsd, 1.08420m, (_, _) => 1);
        result.Should().Be(10.0m);
    }

    [Fact]
    public void PipValue_Case2_BaseEqualsAccount()
    {
        var result = PipCalculator.PipValuePerLot(UsdJpy, 149.50m, (_, _) => 1);
        result.Should().BeApproximately(6.69m, 0.01m);
    }

    [Fact]
    public void PipValue_Case3_CrossPair()
    {
        var result = PipCalculator.PipValuePerLot(GbpJpy, 189.50m, (from, to) =>
        {
            if (from == "JPY" && to == "USD") return 1m / 149.50m;
            return 1;
        });
        result.Should().BeApproximately(6.69m, 0.01m);
    }

    [Fact]
    public void NeverUsesDoubleForPriceArithmetic()
    {
        var distance = PipCalculator.Distance(new Price(1.08420m), new Price(1.08210m), EurUsd);
        distance.Value.Should().Be(21);
    }

    // P0.1: R must be computed against the INITIAL stop distance (risk taken at entry), never the
    // current/trailed stop at close time — a breakeven or trailing move must not change R.

    [Fact]
    public void RMultiple_Long_UsesInitialStopDistance()
    {
        var entry = new Price(1.08500m);
        var initialStop = new Price(1.08210m); // 29 pips risk
        var exit = new Price(1.09080m); // 58 pips reward -> exactly 2R

        var r = PipCalculator.RMultiple(TradeDirection.Long, entry, exit, initialStop);

        r.Should().BeApproximately(2.0, 0.001);
    }

    [Fact]
    public void RMultiple_Short_UsesInitialStopDistance()
    {
        var entry = new Price(1.16285m);
        var initialStop = new Price(1.16635m); // 35 pips risk (stop above entry for a short)
        var exit = new Price(1.15935m); // 35 pips reward -> exactly 1R

        var r = PipCalculator.RMultiple(TradeDirection.Short, entry, exit, initialStop);

        r.Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void RMultiple_ZeroRiskDistance_ReturnsZero_NotInfinity()
    {
        var entry = new Price(1.08500m);
        var r = PipCalculator.RMultiple(TradeDirection.Long, entry, new Price(1.09000m), entry);
        r.Should().Be(0);
    }

    [Fact]
    public void RMultiple_IgnoresStopMovedToBreakeven_RegressionForP01()
    {
        // Regression for the P0.1 bug: R used to be computed against PositionState.CurrentStopLoss —
        // the trailed/breakeven stop AT CLOSE — instead of the stop the trade actually risked at entry.
        // A trade whose stop was moved to breakeven before hitting TP showed a corrupted R (the DB
        // evidence: TP exits averaged R=6.997 against a configured 2.0-3.0 RR). Passing the moved stop
        // must NOT change the result computed from the initial stop.
        var entry = new Price(1.08500m);
        var initialStop = new Price(1.08210m); // 29 pips risk at entry
        var movedStopAtClose = new Price(1.08500m); // breakeven-moved stop by the time the trade closed
        var exit = new Price(1.09080m); // 58 pips reward

        var correctR = PipCalculator.RMultiple(TradeDirection.Long, entry, exit, initialStop);
        var ifWronglyUsingMovedStop = PipCalculator.RMultiple(TradeDirection.Long, entry, exit, movedStopAtClose);

        correctR.Should().BeApproximately(2.0, 0.001);
        ifWronglyUsingMovedStop.Should().Be(0); // proves the two stops give different (wrong-vs-right) results
    }
}

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
}

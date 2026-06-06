namespace TradingEngine.Tests.Unit.Phase3ATests;

[Trait("Category", "Services")]
public sealed class PipCalculatorPhase3ATests
{
    [Fact] // T-8
    public void UsdJpyPipValue_NotTenDollars()
    {
        var usdJpy = new SymbolInfo(Symbol.Parse("USDJPY"), SymbolCategory.Forex, "USD", "JPY",
            0.01m, 0.001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.01m);

        var pipValue = PipCalculator.PipValuePerLot(usdJpy, 149.50m, (from, to) =>
        {
            if (from == "JPY" && to == "USD") return 1m / 149.50m;
            return 1;
        });

        pipValue.Should().BeApproximately(6.69m, 0.10m);
        pipValue.Should().NotBe(10.0m);
    }
}

namespace TradingEngine.Tests.Unit.ServiceTests;

[Trait("Category", "Services")]
public sealed class TrailingStopTests
{
    private static readonly SymbolInfo EurUsd = new(
        Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    [Fact]
    public void StepTrail_NeverMovesBackward()
    {
        var pos = new Position(Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"),
            TradeDirection.Long, 0.1m, new Price(1.08500m), new Price(1.08350m), null,
            DateTime.UtcNow, "test");

        var result = TrailingHelpers.StepTrail(pos, 1.08400m, 1.08410m, new Pips(10), EurUsd);
        result.Should().BeNull();
    }

    [Fact]
    public void Breakeven_OnlyFiresOnce()
    {
        var pos = new Position(Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"),
            TradeDirection.Long, 0.1m, new Price(1.08500m), new Price(1.08510m), null,
            DateTime.UtcNow, "test");

        var result = TrailingHelpers.Breakeven(pos, 1.08800m, 1.08810m, 1.0, new Pips(1), EurUsd);
        result.Should().BeNull();
    }
}

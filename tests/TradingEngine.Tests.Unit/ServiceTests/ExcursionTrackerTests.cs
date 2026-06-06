namespace TradingEngine.Tests.Unit.ServiceTests;

[Trait("Category", "Services")]
public sealed class ExcursionTrackerTests
{
    private static readonly SymbolInfo EurUsd = new(
        Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    [Fact]
    public void Mae_TracksWorstAdverse()
    {
        var tracker = new ExcursionTracker();
        var position = new Position(Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"),
            TradeDirection.Long, 0.1m, new Price(1.08500m), new Price(1.08300m), null,
            DateTime.UtcNow, "test");

        tracker.Update(position, new Tick(Symbol.Parse("EURUSD"), 1.08400m, 1.08410m, DateTime.UtcNow), EurUsd);
        var mae = tracker.Mae.Value;
        mae.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Mfe_TracksBestFavorable()
    {
        var tracker = new ExcursionTracker();
        var position = new Position(Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"),
            TradeDirection.Long, 0.1m, new Price(1.08500m), new Price(1.08300m), null,
            DateTime.UtcNow, "test");

        tracker.Update(position, new Tick(Symbol.Parse("EURUSD"), 1.08600m, 1.08610m, DateTime.UtcNow), EurUsd);
        var mfe = tracker.Mfe.Value;
        mfe.Should().BeGreaterThan(0);
    }
}

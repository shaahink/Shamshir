namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Services")]
public sealed class PositionManagerPhase3BTests
{
    private static readonly ISymbolInfoRegistry Registry = CreateRegistry();

    private static ISymbolInfoRegistry CreateRegistry()
    {
        var r = new SymbolInfoRegistry();
        r.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        return r;
    }

    [Fact] // T-9
    public void TrailingStop_AdvancesSlForWinningLong()
    {
        var pm = new PositionManager(Registry);
        var pos = new Position(Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long,
            0.1m, new Price(1.08500m), new Price(1.08300m), null, DateTime.UtcNow, "test");

        pm.RegisterPosition(pos, new PositionManagementConfig(
            "test", new TrailingConfig(TrailingMethod.StepPips, 10, 0, 0),
            false, 0, new Pips(0), new Money(100, "USD")));

        var tick = new Tick(Symbol.Parse("EURUSD"), 1.08650m, 1.08660m, DateTime.UtcNow);
        var mods = pm.Evaluate(pos, tick, []);

        mods.Should().HaveCount(1);
        (mods[0] as MoveStopLoss)!.NewStopLoss.Value.Should().BeGreaterThan(pos.CurrentStopLoss.Value);
    }

    [Fact] // T-10
    public void Breakeven_TriggersAtRmultiple()
    {
        var pm = new PositionManager(Registry);
        var pos = new Position(Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long,
            0.1m, new Price(1.08500m), new Price(1.08300m), null, DateTime.UtcNow, "test");

        pm.RegisterPosition(pos, new PositionManagementConfig(
            "test", new TrailingConfig(TrailingMethod.StepPips, 500, 0, 0), // wide step so it won't fire
            true, 1.0, new Pips(1), new Money(100, "USD")));

        var tick = new Tick(Symbol.Parse("EURUSD"), 1.08700m, 1.08710m, DateTime.UtcNow);
        var mods = pm.Evaluate(pos, tick, []);

        mods.Should().HaveCount(1);
    }
}

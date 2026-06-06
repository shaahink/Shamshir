namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Services")]
public sealed class PositionManagerMultiMethodTests
{
    private static ISymbolInfoRegistry MakeRegistry()
    {
        var r = new SymbolInfoRegistry();
        r.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        return r;
    }

    private static Position MakePos() => new(
        Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long, 0.1m,
        new Price(1.08500m), new Price(1.08300m), null, DateTime.UtcNow, "test");

    [Fact]
    public void StepTrail_AdvancesSl()
    {
        var pm = new PositionManager(MakeRegistry());
        var pos = MakePos();
        pm.RegisterPosition(pos, new PositionManagementConfig(
            "test", new TrailingConfig(TrailingMethod.StepPips, 10, 0, 0),
            false, 0, new Pips(0), new Money(100, "USD")));
        var mods = pm.Evaluate(pos, new Tick(Symbol.Parse("EURUSD"), 1.08650m, 1.08660m, DateTime.UtcNow), []);
        mods.Should().HaveCount(1);
    }

    [Fact]
    public void AtrTrail_DoesNotThrow()
    {
        var pm = new PositionManager(MakeRegistry());
        var pos = MakePos();
        pm.RegisterPosition(pos, new PositionManagementConfig(
            "test", new TrailingConfig(TrailingMethod.AtrMultiple, 0, 1.5, 0),
            false, 0, new Pips(0), new Money(100, "USD")));
        var mods = pm.Evaluate(pos, new Tick(Symbol.Parse("EURUSD"), 1.08800m, 1.08810m, DateTime.UtcNow), []);
        // ATR trail may or may not fire depending on mock bars, but should not throw
        mods.Should().BeAssignableTo<IReadOnlyList<PositionModification>>();
    }

    [Fact]
    public void BreakevenThenTrail_FiresOnProfit()
    {
        var pm = new PositionManager(MakeRegistry());
        var pos = MakePos();
        pm.RegisterPosition(pos, new PositionManagementConfig(
            "test", new TrailingConfig(TrailingMethod.BreakevenThenTrail, 0, 0, 1.0),
            false, 1.0, new Pips(1), new Money(100, "USD")));
        var mods = pm.Evaluate(pos, new Tick(Symbol.Parse("EURUSD"), 1.08800m, 1.08810m, DateTime.UtcNow), []);
        mods.Should().HaveCount(1);
    }
}

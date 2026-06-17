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

    private static PositionManager MakePm()
    {
        var indicators = Substitute.For<IIndicatorService>();
        var logger = Substitute.For<ILogger<PositionManager>>();
        return new PositionManager(MakeRegistry(), indicators, logger);
    }

    [Fact]
    public void StepTrail_AdvancesSl()
    {
        var pm = MakePm();
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
        var pm = MakePm();
        var pos = MakePos();
        pm.RegisterPosition(pos, new PositionManagementConfig(
            "test", new TrailingConfig(TrailingMethod.AtrMultiple, 0, 1.5, 0),
            false, 0, new Pips(0), new Money(100, "USD")));
        var mods = pm.Evaluate(pos, new Tick(Symbol.Parse("EURUSD"), 1.08800m, 1.08810m, DateTime.UtcNow), []);
        mods.Should().BeAssignableTo<IReadOnlyList<PositionModification>>();
    }

    [Fact]
    public void BreakevenThenTrail_FiresOnProfit()
    {
        var pm = MakePm();
        var pos = MakePos();
        pm.RegisterPosition(pos, new PositionManagementConfig(
            "test", new TrailingConfig(TrailingMethod.BreakevenThenTrail, 0, 0, 1.0),
            false, 1.0, new Pips(1), new Money(100, "USD")));
        var mods = pm.Evaluate(pos, new Tick(Symbol.Parse("EURUSD"), 1.08800m, 1.08810m, DateTime.UtcNow), []);
        mods.Should().HaveCount(1);
    }

    // Regression lock: breakeven and trailing must COEXIST. The old code guarded every trailing branch
    // with !_beApplied, so once breakeven engaged the stop never trailed again.
    [Fact]
    public void Breakeven_then_trailing_continue_past_entry()
    {
        var indicators = Substitute.For<IIndicatorService>();
        indicators.Atr(Arg.Any<IReadOnlyList<Bar>>(), Arg.Any<int>()).Returns(0.0010); // 10-pip ATR
        var pm = new PositionManager(MakeRegistry(), indicators, Substitute.For<ILogger<PositionManager>>());
        var sym = Symbol.Parse("EURUSD");

        // entry 1.08500, 20-pip (1R) stop at 1.08300; BE@1R + 2.5x ATR trail (offset = 25 pips).
        var pos = new Position(Guid.NewGuid(), Guid.NewGuid(), sym, TradeDirection.Long, 0.1m,
            new Price(1.08500m), new Price(1.08300m), null, DateTime.UtcNow, "test");
        pm.RegisterPosition(pos, new PositionManagementConfig(
            "test", new TrailingConfig(TrailingMethod.AtrMultiple, 0, 2.5, 1.0),
            true, 1.0, new Pips(1), new Money(100, "USD")));

        var bars = Enumerable.Range(0, 20)
            .Select(i => new Bar(sym, Timeframe.H1, DateTime.UtcNow.AddHours(i), 1.085m, 1.086m, 1.084m, 1.085m, 1000))
            .ToList();

        // +20 pips (1R): breakeven engages; the 25-pip trail is still below entry.
        var step1 = pm.Evaluate(pos, new Tick(sym, 1.08700m, 1.08710m, DateTime.UtcNow), bars);
        var sl1 = ((MoveStopLoss)step1.Single()).NewStopLoss.Value;
        sl1.Should().BeGreaterThanOrEqualTo(1.08500m).And.BeLessThan(1.08600m);

        // +50 pips: trailing CONTINUES past breakeven (old code froze here).
        var pos2 = pos with { CurrentStopLoss = new Price(sl1) };
        var step2 = pm.Evaluate(pos2, new Tick(sym, 1.09000m, 1.09010m, DateTime.UtcNow), bars);
        var sl2 = ((MoveStopLoss)step2.Single()).NewStopLoss.Value;
        sl2.Should().BeGreaterThan(sl1);
        sl2.Should().BeApproximately(1.09000m - 0.0025m, 0.0003m);
    }
}

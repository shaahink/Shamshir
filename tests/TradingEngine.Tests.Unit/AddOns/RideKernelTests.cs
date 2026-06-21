namespace TradingEngine.Tests.Unit.AddOns;

/// <summary>
/// iter-38 A5 (Ride): while enabled and ADX is above the floor, the ATR trail uses the relaxed (wider)
/// multiple — giving a runner more room (a looser stop) — and reverts to the configured multiple when ADX
/// falls back. Off ⇒ always the configured multiple (golden byte-identical).
/// </summary>
[Trait("Category", "AddOns")]
[Trait("Category", "Services")]
public sealed class RideKernelTests
{
    private static readonly ISymbolInfoRegistry Registry = BuildRegistry();

    private static ISymbolInfoRegistry BuildRegistry()
    {
        var r = new SymbolInfoRegistry();
        r.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        return r;
    }

    private static readonly IReadOnlyList<Bar> Bars =
    [
        new Bar(Symbol.Parse("EURUSD"), Timeframe.H1, DateTime.UtcNow.AddHours(-2), 1.10m, 1.10m, 1.10m, 1.10m, 0),
        new Bar(Symbol.Parse("EURUSD"), Timeframe.H1, DateTime.UtcNow.AddHours(-1), 1.10m, 1.10m, 1.10m, 1.10m, 0),
    ];

    private static decimal TrailedStop(double adx)
    {
        var indicators = Substitute.For<IIndicatorService>();
        indicators.Atr(Arg.Any<IReadOnlyList<Bar>>(), Arg.Any<int>()).Returns(0.0010);
        indicators.Adx(Arg.Any<IReadOnlyList<Bar>>(), Arg.Any<int>()).Returns(adx);

        var pm = new PositionManager(Registry, indicators, Substitute.For<ILogger<PositionManager>>());
        var pos = new Position(Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long,
            0.1m, new Price(1.10000m), new Price(1.09000m), null, DateTime.UtcNow, "t");

        pm.RegisterPosition(pos, new PositionManagementConfig("t",
            new TrailingConfig(TrailingMethod.AtrMultiple, 0, 2.0, 0)
            {
                RideEnabled = true,
                RideAdxFloor = 25,
                RideRelaxedAtrMultiple = 4.0,
            },
            false, 0, new Pips(0), new Money(100, "USD")));

        var tick = new Tick(Symbol.Parse("EURUSD"), 1.11000m, 1.11010m, DateTime.UtcNow);
        var mods = pm.Evaluate(pos, tick, Bars);
        return ((MoveStopLoss)mods[0]).NewStopLoss.Value;
    }

    [Fact]
    public void Ride_widens_trail_above_adx_floor_and_reverts_below()
    {
        var strong = TrailedStop(30); // ADX > floor ⇒ relaxed multiple (4.0) ⇒ wider/looser (lower) stop
        var weak = TrailedStop(20);   // ADX < floor ⇒ configured multiple (2.0) ⇒ tighter (higher) stop

        strong.Should().BeLessThan(weak);
        weak.Should().BeGreaterThan(1.09000m);
    }
}

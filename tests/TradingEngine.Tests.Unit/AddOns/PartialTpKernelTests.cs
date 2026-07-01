namespace TradingEngine.Tests.Unit.AddOns;

/// <summary>
/// iter-38 A4 (PartialTp): the PositionManager emits a single <see cref="PartialClose"/> the first time the
/// position reaches the trigger R-multiple, sized to the close-fraction (floored to the lot step), and never
/// again. The remainder keeps trailing. Off (default) ⇒ no partial. (The kernel wiring that executes the
/// PartialClose against the venue is A4b.)
/// </summary>
[Trait("Category", "AddOns")]
[Trait("Category", "Services")]
public sealed class PartialTpKernelTests
{
    private static readonly ISymbolInfoRegistry Registry = BuildRegistry();

    private static ISymbolInfoRegistry BuildRegistry()
    {
        var r = new SymbolInfoRegistry();
        r.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        return r;
    }

    private static (PositionManager Pm, Position Pos) Setup()
    {
        var pm = new PositionManager(Registry, Substitute.For<IIndicatorService>(), Substitute.For<ILogger<PositionManager>>());
        var pos = new Position(Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long,
            0.1m, new Price(1.10000m), new Price(1.09900m), null, DateTime.UtcNow, "t"); // initial SL distance = 0.0010 (10 pips)

        pm.RegisterPosition(pos, new PositionManagementConfig("t",
            new TrailingConfig(TrailingMethod.None, 0, 0, 0), false, 0, new Pips(0), new Money(100, "USD"))
        {
            PartialTpEnabled = true,
            PartialTpTriggerR = 1.0,
            PartialTpCloseFraction = 0.5,
        });
        return (pm, pos);
    }

    private static Tick At(decimal bid) => new(Symbol.Parse("EURUSD"), bid, bid + 0.0001m, DateTime.UtcNow);

    [Fact]
    public void Closes_half_once_at_trigger_R_then_not_again()
    {
        var (pm, pos) = Setup();

        // R = 0.5 (5 pips of the 10-pip initial risk) — below trigger, no partial.
        pm.Evaluate(pos, At(1.10050m), []).OfType<PartialClose>().Should().BeEmpty();

        // R = 1.0 (10 pips) — fires once; closes 0.1 * 0.5 = 0.05 lots.
        var partial = pm.Evaluate(pos, At(1.10100m), []).OfType<PartialClose>().Single();
        partial.CloseLots.Should().Be(0.05m);
        partial.Reason.Should().Be("PARTIAL");

        // Already partialed — even at higher R, no second partial.
        pm.Evaluate(pos, At(1.10200m), []).OfType<PartialClose>().Should().BeEmpty();
    }
}

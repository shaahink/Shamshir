using TradingEngine.Services.AddOns;

namespace TradingEngine.Tests.Unit.AddOns;

/// <summary>
/// iter-38 A3: resolve-at-entry semantics. Auto add-ons take the tuner's numbers; Custom add-ons keep their
/// stored numbers; a config with no add-ons enabled is a pass-through (identical baseline) — which is what
/// keeps the default/golden path byte-identical when the resolver is wired into the entry seam.
/// </summary>
[Trait("Category", "AddOns")]
[Trait("Speed", "Fast")]
public sealed class AddOnResolverTests
{
    private static readonly VolatilityContext Vol = new(AtrPips: 15, TypicalSpreadPips: 1.2, ReferenceAtrPips: 10);
    private readonly AddOnResolver _resolver = new();

    [Fact]
    public void All_off_is_passthrough()
    {
        var opts = new PositionManagementOptions(); // every add-on off by default
        var res = _resolver.ResolveAtEntry(opts, Timeframe.H1, Vol);

        res.Resolved.Trailing.AtrMultiple.Should().Be(opts.Trailing.AtrMultiple);
        res.Resolved.Breakeven.TriggerRMultiple.Should().Be(opts.Breakeven.TriggerRMultiple);
        res.Resolved.StopLoss.AtrMultiple.Should().Be(opts.StopLoss.AtrMultiple);
        res.Resolved.TakeProfit.RrMultiple.Should().Be(opts.TakeProfit.RrMultiple);
        res.Resolved.Ride.Should().BeNull();
        res.Resolved.PartialTp.Should().BeNull();
        res.Resolved.DynamicSlTp.Should().BeNull();
    }

    [Fact]
    public void Auto_trailing_takes_tuner_values()
    {
        var opts = new PositionManagementOptions
        {
            Trailing = new TrailingOptions { Enabled = true, Mode = AddOnMode.Auto, Method = "AtrTrailing", AtrMultiple = 999 },
        };
        var tuned = AddOnAutoTuner.Tune(Timeframe.H1, Vol);
        var res = _resolver.ResolveAtEntry(opts, Timeframe.H1, Vol);

        res.Resolved.Trailing.AtrMultiple.Should().Be(tuned.TrailingAtrMultiple);
        res.Resolved.Trailing.AtrMultiple.Should().NotBe(999);
    }

    [Fact]
    public void Custom_trailing_keeps_stored_values()
    {
        var opts = new PositionManagementOptions
        {
            Trailing = new TrailingOptions { Enabled = true, Mode = AddOnMode.Custom, Method = "AtrTrailing", AtrMultiple = 2.7 },
        };
        var res = _resolver.ResolveAtEntry(opts, Timeframe.H1, Vol);

        res.Resolved.Trailing.AtrMultiple.Should().Be(2.7);
    }
}

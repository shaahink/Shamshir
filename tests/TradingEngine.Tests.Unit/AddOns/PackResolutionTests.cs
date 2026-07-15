namespace TradingEngine.Tests.Unit.AddOns;

/// <summary>
/// iter-38 PK2 / D1: applying a reusable add-on pack over a strategy's own add-ons. The pack REPLACES the
/// enrichments (breakeven/trailing/partial/ride/dynamic) but the mandatory baseline SL/TP stays from the
/// strategy (D4). No pack ⇒ the strategy's own add-ons stand; neither ⇒ baseline only.
/// </summary>
[Trait("Category", "AddOns")]
[Trait("Speed", "Fast")]
public sealed class PackResolutionTests
{
    private readonly EffectiveConfigResolver _resolver = new();

    private static AddOnPack Pack(PositionManagementOptions addOns) => new("p", "Pack", null, addOns);

    [Fact]
    public void Pack_replaces_strategy_addons_but_keeps_baseline_sltp()
    {
        var strategy = new PositionManagementOptions
        {
            StopLoss = new SlOptions { AtrMultiple = 2.0 },
            TakeProfit = new TpOptions { RrMultiple = 2.5 },
            Trailing = new TrailingOptions { Enabled = false },
            PartialTp = null,
        };
        var pack = Pack(new PositionManagementOptions
        {
            Trailing = new TrailingOptions { Enabled = true, Mode = AddOnMode.Auto, Method = "AtrMultiple" },
            PartialTp = new PartialTpOptions { Enabled = true, Mode = AddOnMode.Auto },
        });

        var result = _resolver.ApplyPack(strategy, pack);

        result.Should().NotBeNull();
        result!.Trailing.Enabled.Should().BeTrue();      // pack's trailing wins
        result.PartialTp!.Enabled.Should().BeTrue();     // pack adds a partial the strategy lacked
        result.StopLoss.AtrMultiple.Should().Be(2.0);    // baseline SL kept from the strategy
        result.TakeProfit.RrMultiple.Should().Be(2.5);   // baseline TP kept from the strategy
    }

    [Fact]
    public void Absent_pack_keeps_strategy_addons()
    {
        var strategy = new PositionManagementOptions { Breakeven = new BreakevenOptions { Enabled = true } };
        _resolver.ApplyPack(strategy, null).Should().BeSameAs(strategy);
    }

    [Fact]
    public void No_strategy_addons_and_no_pack_is_baseline_only()
        => _resolver.ApplyPack(null, null).Should().BeNull();
}

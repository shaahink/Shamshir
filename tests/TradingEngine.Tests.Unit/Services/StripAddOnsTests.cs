namespace TradingEngine.Tests.Unit.Services;

/// <summary>
/// iter-redesign P3.2 — a "no add-ons (raw)" run must strip every enrichment add-on while preserving the
/// strategy's baseline SL/TP, so the owner can run a raw strategy and watch the unmasked drawdown.
/// </summary>
public sealed class StripAddOnsTests
{
    [Fact]
    public void StripAddOns_DisablesEveryAddOn_KeepsBaselineSlTp()
    {
        var fullyLoaded = new PositionManagementOptions
        {
            StopLoss = new SlOptions { Method = "AtrMultiple", AtrMultiple = 2.5, MaxPips = 80 },
            TakeProfit = new TpOptions { Method = "RrMultiple", RrMultiple = 3.0 },
            Breakeven = new BreakevenOptions { Enabled = true, TriggerRMultiple = 1.0 },
            Trailing = new TrailingOptions { Enabled = true, Method = "AtrMultiple", AtrMultiple = 2.0 },
            Ride = new RideOptions { Enabled = true },
            PartialTp = new PartialTpOptions { Enabled = true, CloseFraction = 0.5 },
            DynamicSlTp = new DynamicSlTpOptions { Enabled = true, AtrMultipleSl = 1.8 },
        };

        var stripped = EffectiveConfigResolver.StripAddOns(fullyLoaded);

        // Baseline SL/TP preserved verbatim.
        stripped.StopLoss.Method.Should().Be("AtrMultiple");
        stripped.StopLoss.AtrMultiple.Should().Be(2.5);
        stripped.StopLoss.MaxPips.Should().Be(80);
        stripped.TakeProfit.Method.Should().Be("RrMultiple");
        stripped.TakeProfit.RrMultiple.Should().Be(3.0);

        // Every add-on disabled / nulled.
        stripped.Breakeven.Enabled.Should().BeFalse();
        stripped.Trailing.Enabled.Should().BeFalse();
        stripped.Trailing.Method.Should().Be("None");
        stripped.Ride.Should().BeNull();
        stripped.PartialTp.Should().BeNull();
        stripped.DynamicSlTp.Should().BeNull();
    }

    [Fact]
    public void StripAddOns_NullInput_ReturnsBareDefaults()
    {
        var stripped = EffectiveConfigResolver.StripAddOns(null);

        stripped.Should().NotBeNull();
        stripped.Breakeven.Enabled.Should().BeFalse();
        stripped.Trailing.Enabled.Should().BeFalse();
        stripped.Ride.Should().BeNull();
        stripped.PartialTp.Should().BeNull();
        stripped.DynamicSlTp.Should().BeNull();
    }
}

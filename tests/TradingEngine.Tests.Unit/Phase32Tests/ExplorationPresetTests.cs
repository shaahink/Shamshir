namespace TradingEngine.Tests.Unit.Services;

using TradingEngine.Services;

/// <summary>
/// P3.2: <see cref="EffectiveConfigResolver.ApplyExplorationPreset"/> must force SL=ATR×4, TP=none,
/// and disable all enrichments so the strategy runs bare for exit-lab calibration.
/// </summary>
public sealed class ExplorationPresetTests
{
    [Fact]
    public void ApplyExplorationPreset_Forces_WideSL_And_NoTP_And_NoAddOns()
    {
        var original = new PositionManagementOptions
        {
            StopLoss = new SlOptions { Method = "AtrMultiple", AtrMultiple = 1.5, MaxPips = 80 },
            TakeProfit = new TpOptions { Method = "RrMultiple", RrMultiple = 2.0 },
            Breakeven = new BreakevenOptions { Enabled = true, TriggerRMultiple = 1.0 },
            Trailing = new TrailingOptions { Enabled = true, Method = "AtrMultiple", AtrMultiple = 2.0 },
            Ride = new RideOptions { Enabled = true },
            PartialTp = new PartialTpOptions { Enabled = true, CloseFraction = 0.5 },
            DynamicSlTp = new DynamicSlTpOptions { Enabled = true, AtrMultipleSl = 1.8 },
        };

        var preset = EffectiveConfigResolver.ApplyExplorationPreset(original);

        // Wide SL
        preset.StopLoss.Method.Should().Be("AtrMultiple");
        preset.StopLoss.AtrMultiple.Should().Be(4.0);
        preset.StopLoss.MaxPips.Should().Be(80); // preserved from original cap

        // No TP
        preset.TakeProfit.Method.Should().Be("None");

        // All add-ons disabled
        preset.Breakeven.Enabled.Should().BeFalse();
        preset.Trailing.Enabled.Should().BeFalse();
        preset.Ride.Should().BeNull();
        preset.PartialTp.Should().BeNull();
        preset.DynamicSlTp.Should().BeNull();
    }

    [Fact]
    public void ApplyExplorationPreset_Upon_AlreadyStripped_ResultsInSame()
    {
        var stripped = EffectiveConfigResolver.StripAddOns(null);
        var preset = EffectiveConfigResolver.ApplyExplorationPreset(stripped);

        preset.StopLoss.AtrMultiple.Should().Be(4.0);
        preset.TakeProfit.Method.Should().Be("None");
    }

    [Fact]
    public void ApplyExplorationPreset_ChainedAfter_StripAddOns_IsCorrect()
    {
        var original = new PositionManagementOptions
        {
            StopLoss = new SlOptions { AtrMultiple = 1.5 },
            Breakeven = new BreakevenOptions { Enabled = true },
        };

        var stripped = EffectiveConfigResolver.StripAddOns(original);
        var preset = EffectiveConfigResolver.ApplyExplorationPreset(stripped);

        var directlyFromOriginal = EffectiveConfigResolver.ApplyExplorationPreset(original);

        preset.StopLoss.AtrMultiple.Should().Be(directlyFromOriginal.StopLoss.AtrMultiple);
        preset.TakeProfit.Method.Should().Be(directlyFromOriginal.TakeProfit.Method);
    }
}

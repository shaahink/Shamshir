using TradingEngine.Services.AddOns;

namespace TradingEngine.Tests.Unit.AddOns;

/// <summary>
/// iter-38 Stream A2 / D2: pins the pure auto-tuner contract — deterministic, monotonic in timeframe and ATR,
/// and clamped. The constants may be recalibrated, but these invariants must hold.
/// </summary>
[Trait("Category", "AddOns")]
[Trait("Speed", "Fast")]
public sealed class AddOnAutoTunerTests
{
    private static VolatilityContext Vol(double atr, double refAtr = 0, double spread = 1.0)
        => new(AtrPips: atr, TypicalSpreadPips: spread, ReferenceAtrPips: refAtr);

    [Fact]
    public void Is_deterministic()
    {
        var a = AddOnAutoTuner.Tune(Timeframe.H1, Vol(10, 10));
        var b = AddOnAutoTuner.Tune(Timeframe.H1, Vol(10, 10));
        a.Should().Be(b);
    }

    [Fact]
    public void Trailing_base_is_monotonic_in_timeframe_at_neutral_vol()
    {
        var m15 = AddOnAutoTuner.Tune(Timeframe.M15, Vol(10)).TrailingAtrMultiple;
        var h1 = AddOnAutoTuner.Tune(Timeframe.H1, Vol(10)).TrailingAtrMultiple;
        var h4 = AddOnAutoTuner.Tune(Timeframe.H4, Vol(10)).TrailingAtrMultiple;
        var d1 = AddOnAutoTuner.Tune(Timeframe.D1, Vol(10)).TrailingAtrMultiple;

        h1.Should().BeGreaterThanOrEqualTo(m15);
        h4.Should().BeGreaterThanOrEqualTo(h1);
        d1.Should().BeGreaterThanOrEqualTo(h4);
    }

    [Fact]
    public void Trailing_is_non_decreasing_in_atr()
    {
        var calm = AddOnAutoTuner.Tune(Timeframe.H1, Vol(atr: 10, refAtr: 10)).TrailingAtrMultiple;
        var wild = AddOnAutoTuner.Tune(Timeframe.H1, Vol(atr: 30, refAtr: 10)).TrailingAtrMultiple;
        wild.Should().BeGreaterThanOrEqualTo(calm);
    }

    [Fact]
    public void Neutral_vol_factor_when_reference_unknown()
    {
        // ReferenceAtrPips == 0 => volFactor neutral (1.0) => trailing == tf base (2.5 for H1).
        AddOnAutoTuner.Tune(Timeframe.H1, Vol(atr: 9999, refAtr: 0)).TrailingAtrMultiple
            .Should().Be(2.5);
    }

    [Theory]
    [InlineData(Timeframe.M15, 0.001)]
    [InlineData(Timeframe.M15, 100000)]
    [InlineData(Timeframe.D1, 0.001)]
    [InlineData(Timeframe.D1, 100000)]
    public void Outputs_are_clamped(Timeframe tf, double atr)
    {
        var r = AddOnAutoTuner.Tune(tf, Vol(atr: atr, refAtr: 10));
        r.TrailingAtrMultiple.Should().BeInRange(1.5, 4.0);
        r.BreakevenTriggerR.Should().BeInRange(0.6, 1.6);
        r.DynamicSlAtrMultiple.Should().BeInRange(1.0, 2.5);
        r.DynamicTpRrMultiple.Should().BeInRange(1.5, 3.0);
        r.TrailingStepPips.Should().BeGreaterThanOrEqualTo(1.0);
    }
}

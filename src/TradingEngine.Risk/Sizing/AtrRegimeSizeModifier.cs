namespace TradingEngine.Risk.Sizing;

public sealed class AtrRegimeSizeModifier : ISizeModifier
{
    public string Name => "ATR_Regime";

    public double ComputeScale(SizeModifierContext context)
    {
        var opts = context.Profile.SizeModifiers.AtrRegime;
        if (!opts.Enabled) return 1.0;
        if (context.CurrentAtr is not { } currentAtr || currentAtr <= 0) return 1.0;

        var baseline = context.AtrBaseline;
        if (baseline.Count == 0) return 1.0;

        var avgBaseline = baseline.Average();
        if (avgBaseline <= 0) return 1.0;

        var ratio = currentAtr / avgBaseline;

        if (ratio >= 3.0) return opts.ExtremeAtrSizeScale;
        if (ratio >= opts.HighAtrMultiple) return opts.HighAtrSizeScale;
        if (ratio <= opts.LowAtrMultiple) return opts.LowAtrSizeScale;
        return 1.0;
    }
}

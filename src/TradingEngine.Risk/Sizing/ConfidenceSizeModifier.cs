namespace TradingEngine.Risk.Sizing;

public sealed class ConfidenceSizeModifier : ISizeModifier
{
    public string Name => "Confidence";

    public double ComputeScale(SizeModifierContext context)
    {
        var opts = context.Profile.SizeModifiers.Confidence;
        if (!opts.Enabled) return 1.0;

        if (context.StrategyLossStreak >= opts.LossStreakThreshold)
            return opts.LossStreakScale;

        if (context.StrategyWinStreak >= opts.WinStreakThreshold)
            return opts.WinStreakScale;

        return 1.0;
    }
}

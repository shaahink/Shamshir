namespace TradingEngine.Risk.Sizing;

public sealed class DrawdownSizeModifier : ISizeModifier
{
    public string Name => "Drawdown";

    public double ComputeScale(SizeModifierContext context)
    {
        return (double)DrawdownScaler.ComputeScaleFactor(
            context.Equity.CurrentMaxDrawdown,
            (decimal)context.Profile.MaxTotalDrawdownPercent,
            context.Profile.DrawdownScaleThreshold,
            context.Profile.DrawdownScaleFloor);
    }
}

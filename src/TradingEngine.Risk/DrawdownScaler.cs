namespace TradingEngine.Risk;

public static class DrawdownScaler
{
    public static double ComputeScaleFactor(
        decimal currentDrawdownFraction,
        decimal maxDrawdownLimit,
        double scaleThreshold,
        double scaleFloor)
    {
        if (maxDrawdownLimit <= 0)
            return 1.0;

        var ddRatio = (double)(currentDrawdownFraction / maxDrawdownLimit);

        if (ddRatio <= scaleThreshold)
            return 1.0;

        if (ddRatio >= 1.0)
            return scaleFloor;

        var range = 1.0 - scaleThreshold;
        var position = (ddRatio - scaleThreshold) / range;
        return 1.0 - (position * (1.0 - scaleFloor));
    }
}

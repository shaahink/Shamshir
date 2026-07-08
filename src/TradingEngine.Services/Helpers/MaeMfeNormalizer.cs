namespace TradingEngine.Services.Helpers;

public static class MaeMfeNormalizer
{
    public static double? ComputeMaeR(double maePips, double stopDistancePips) =>
        stopDistancePips > 0 ? maePips / stopDistancePips : null;

    public static double? ComputeMfeR(double mfePips, double stopDistancePips) =>
        stopDistancePips > 0 ? mfePips / stopDistancePips : null;

    public static (double? MaeR, double? MfeR) Normalize(
        double maePips,
        double mfePips,
        decimal entryPrice,
        decimal stopLoss,
        SymbolInfo symbolInfo)
    {
        var rawDistance = Math.Abs(stopLoss - entryPrice);
        var stopDistancePips = (double)(rawDistance / symbolInfo.PipSize);
        return (ComputeMaeR(maePips, stopDistancePips), ComputeMfeR(mfePips, stopDistancePips));
    }
}

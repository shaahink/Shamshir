namespace TradingEngine.Services.Helpers;

public static class MaeMfeNormalizer
{
    public static double? ComputeMaeR(double maePips, double stopDistancePips)
    {
        if (!double.IsFinite(maePips) || stopDistancePips <= 0)
            return null;
        return maePips / stopDistancePips;
    }

    public static double? ComputeMfeR(double mfePips, double stopDistancePips)
    {
        if (!double.IsFinite(mfePips) || stopDistancePips <= 0)
            return null;
        return mfePips / stopDistancePips;
    }

    public static (double? MaeR, double? MfeR) Normalize(
        double maePips,
        double mfePips,
        decimal entryPrice,
        decimal stopLoss,
        SymbolInfo symbolInfo)
    {
        if (symbolInfo.PipSize <= 0)
            return (null, null);

        if (entryPrice <= 0 || stopLoss <= 0)
            return (null, null);

        if (!double.IsFinite(maePips) || !double.IsFinite(mfePips))
            return (null, null);

        var rawDistance = Math.Abs(stopLoss - entryPrice);
        var stopDistancePips = (double)(rawDistance / symbolInfo.PipSize);
        return (ComputeMaeR(maePips, stopDistancePips), ComputeMfeR(mfePips, stopDistancePips));
    }
}

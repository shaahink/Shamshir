namespace TradingEngine.Engine;

public static class RiskGate
{
    public const string WorstCaseDDWouldBreachDaily = "WorstCaseDDWouldBreachDaily";
    public const string WorstCaseDDWouldBreachOverall = "WorstCaseDDWouldBreachOverall";
    public const string Passed = "Passed";

    public static string ProjectWorstCase(
        decimal currentEquity,
        decimal dailyStartEquity,
        decimal initialBalance,
        decimal maxDailyLossPercent,
        decimal maxTotalLossPercent,
        string drawdownType,
        decimal slPips,
        decimal lots,
        decimal pipValuePerLot,
        IReadOnlyList<ProjectedPosition> openPositions)
    {
        var candidateLoss = slPips * pipValuePerLot * lots;

        var openLosses = 0m;
        for (var i = 0; i < openPositions.Count; i++)
        {
            var p = openPositions[i];
            openLosses += p.SlPips * p.PipValuePerLot * p.Lots;
        }

        var totalWorstCaseLoss = candidateLoss + openLosses;
        var projectedEquity = currentEquity - totalWorstCaseLoss;

        var dailyFloor = dailyStartEquity * (1m - maxDailyLossPercent);
        if (projectedEquity < dailyFloor)
        {
            return WorstCaseDDWouldBreachDaily;
        }

        var drawdownBase = drawdownType == "Trailing" ? currentEquity : initialBalance;
        var maxFloor = drawdownBase * (1m - maxTotalLossPercent);
        if (projectedEquity < maxFloor)
        {
            return WorstCaseDDWouldBreachOverall;
        }

        return Passed;
    }
}

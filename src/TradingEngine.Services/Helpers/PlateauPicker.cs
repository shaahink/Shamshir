namespace TradingEngine.Services.Helpers;

/// <summary>
/// P4.5.1: pure plateau picker for walk-forward sweep results. Given a set of sweep cells
/// (each representing one param grid point), picks the cell at the center of the broadest
/// 3×3-neighborhood plateau ranked by median NetProfit (or WinRate on profit tie).
/// Ties break deterministically: smaller param value first (conservative — lower SL wins).
/// 
/// Accepts a minimal input type so this stays in TradingEngine.Services with zero Web deps.
/// </summary>
public readonly record struct PlateauCell(
    string ParamKey,
    decimal ParamValue,
    decimal NetProfit,
    double WinRatePct,
    decimal MaxDrawdownPct,
    string? Error);

public static class PlateauPicker
{
    public static PlateauCell? Pick(IReadOnlyList<PlateauCell> results)
    {
        if (results.Count == 0) return null;

        var byParam = new Dictionary<string, Dictionary<decimal, PlateauCell>>();
        foreach (var r in results)
        {
            if (r.Error is not null) continue;
            if (!byParam.TryGetValue(r.ParamKey, out var valMap))
            {
                valMap = new Dictionary<decimal, PlateauCell>();
                byParam[r.ParamKey] = valMap;
            }
            valMap[r.ParamValue] = r;
        }

        var paramKey = byParam.Keys.FirstOrDefault();
        if (paramKey is null || !byParam.TryGetValue(paramKey, out var grid)) return null;

        var values = grid.Keys.OrderBy(v => v).ToList();
        if (values.Count == 0) return null;

        if (values.Count < 3)
        {
            return values
                .Select(v => grid[v])
                .OrderByDescending(r => r.NetProfit)
                .ThenBy(r => (double)r.MaxDrawdownPct)
                .ThenBy(r => r.ParamValue)
                .FirstOrDefault();
        }

        return values
            .Select(v => (value: v, score: NeighborhoodScore(grid, values, v)))
            .OrderByDescending(x => x.score.medianNetProfit)
            .ThenByDescending(x => x.score.medianWinRate)
            .ThenBy(x => x.score.paramValue) // smaller value first (conservative)
            .Select(x => grid[x.value])
            .FirstOrDefault();
    }

    private static (decimal medianNetProfit, double medianWinRate, decimal paramValue) NeighborhoodScore(
        Dictionary<decimal, PlateauCell> grid,
        List<decimal> values,
        decimal center)
    {
        var idx = values.IndexOf(center);
        var start = Math.Max(0, idx - 1);
        var end = Math.Min(values.Count - 1, idx + 1);
        var netProfits = new List<decimal>();
        var winRates = new List<double>();

        for (var i = start; i <= end; i++)
        {
            if (grid.TryGetValue(values[i], out var cell))
            {
                netProfits.Add(cell.NetProfit);
                winRates.Add(cell.WinRatePct);
            }
        }

        netProfits.Sort();
        winRates.Sort();
        var n = netProfits.Count;

        var medianProfit = n > 0
            ? n % 2 == 0
                ? (netProfits[n / 2 - 1] + netProfits[n / 2]) / 2m
                : netProfits[n / 2]
            : 0m;

        var medianWin = winRates.Count > 0
            ? winRates.Count % 2 == 0
                ? (winRates[winRates.Count / 2 - 1] + winRates[winRates.Count / 2]) / 2.0
                : winRates[winRates.Count / 2]
            : 0.0;

        return (medianProfit, medianWin, center);
    }
}

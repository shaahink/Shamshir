namespace TradingEngine.Services.Helpers;

public static class DailyPnLComputer
{
    public static IReadOnlyList<decimal> Compute(
        IReadOnlyList<TradeResult> trades, IReadOnlyList<EquitySnapshot> equitySnapshots)
    {
        if (equitySnapshots.Count < 2)
            return [];

        var byDay = equitySnapshots
            .GroupBy(e => e.TimestampUtc.Date)
            .OrderBy(g => g.Key)
            .ToList();

        var results = new List<decimal>();
        decimal? prevEquity = null;

        foreach (var day in byDay)
        {
            var last = day.Last();
            if (prevEquity.HasValue)
                results.Add(last.Equity - prevEquity.Value);
            prevEquity = last.Equity;
        }

        return results;
    }
}

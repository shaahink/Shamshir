using System.Collections.Concurrent;

namespace TradingEngine.Services.ExitLab;

/// <summary>
/// P3.3: exhaustively replays a grid of exit rules against a set of excursion paths, producing
/// per-cell aggregate statistics. Runs in parallel (thousands of cell×trade combinations in
/// milliseconds, per the plan). Pure — no DB, no DI.
/// </summary>
public static class ExitGridEvaluator
{
    public static IEnumerable<ExitRule> GenerateGrid(
        double referenceAtrPips,
        IReadOnlyList<double> slMultiples,
        IReadOnlyList<double?> tpMultiples,
        IReadOnlyList<double?> beTriggers,
        IReadOnlyList<double?> trailMultiples)
    {
        foreach (var sl in slMultiples)
        {
            foreach (var tp in tpMultiples)
            {
                foreach (var be in beTriggers)
                {
                    foreach (var trail in trailMultiples)
                    {
                        yield return new ExitRule
            {
                SlAtrMultiple = sl,
                TpRrMultiple = tp,
                BeTriggerR = be,
                TrailAtrMultiple = trail,
                ReferenceAtrPips = referenceAtrPips,
                        };
                    }
                }
            }
        }
    }

    public static IReadOnlyList<ExitGridCell> Evaluate(
        IReadOnlyList<TradeExcursionInput> trades,
        IReadOnlyList<ExitRule> rules,
        CancellationToken ct = default)
    {
        var cells = new ConcurrentBag<ExitGridCell>();
        Parallel.ForEach(rules, new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        }, rule =>
        {
            // P4.5.3d: partial-TP is not yet modelled — skip rules that try to use it.
            if (rule.PartialTriggerR is not null || rule.PartialCloseFraction is not null)
                return;

            var rValues = new List<double>(trades.Count);
            var totalBars = 0;
            var winCount = 0;
            var minR = double.MaxValue;

            foreach (var trade in trades)
            {
                var outcome = ExitReplayer.Replay(trade, rule);
                rValues.Add(outcome.RMultiple);
                totalBars += outcome.BarsHeld;
                if (outcome.RMultiple > 0) winCount++;
                if (outcome.RMultiple < minR) minR = outcome.RMultiple;
            }

            rValues.Sort();
            var medianR = rValues.Count > 0
                ? rValues.Count % 2 == 0
                    ? (rValues[rValues.Count / 2 - 1] + rValues[rValues.Count / 2]) / 2.0
                    : rValues[rValues.Count / 2]
                : 0.0;

            cells.Add(new ExitGridCell
            {
                Rule = rule,
                Result = new ExitGridResult
                {
                    TradeCount = rValues.Count,
                    WinRate = rValues.Count > 0 ? (double)winCount / rValues.Count : 0,
                    AvgR = rValues.Count > 0 ? rValues.Average() : 0,
                    MedianR = medianR,
                    AvgHoldBars = rValues.Count > 0 ? (double)totalBars / rValues.Count : 0,
                    MaxDrawdownContributionR = rValues.Count > 0 ? minR : 0,
                    TradeRValues = rValues.AsReadOnly(),
                },
            });
        });

        return cells.OrderByDescending(c => c.Result.AvgR).ToList();
    }

    // Pre-built grid dimensions (matches PLAN.md P3.3 "grid of exit rules" recommendation).
    public static readonly double[] DefaultSlMultiples = [0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0, 7.0];
    public static readonly double?[] DefaultTpMultiples = [null, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 4.0, 5.0];
    public static readonly double?[] DefaultBeTriggers = [null, 0.5, 1.0, 1.5, 2.0];
    public static readonly double?[] DefaultTrailMultiples = [null, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 4.0];
}

namespace TradingEngine.Domain.Experiments;

/// <summary>
/// P6.8 — pyramiding policy: model structured adds (add at +1R, move stop to entry) as an exit-lab
/// dimension over excursion paths. Measures whether pyramiding would improve R-multiples for a strategy
/// without ANY engine changes — a diagnosis, not a policy activation.
/// 
/// Method: walk each excursion path point-by-point (same order as ExitReplayer). When price first
/// reaches an add-threshold R-level, simulate a second entry at that level and move the original SL
/// to breakeven. Then compute the combined R-multiple vs the base (no-pyramid) case.
/// </summary>

/// <summary>Domain-pure excursion path point — mirrors ExitLab.ExcursionPoint without the infra dep.</summary>
public readonly record struct PyramidPathPoint(int MinutesSinceEntry, double HiPips, double LoPips);

/// <summary>One pyramiding trial: a single add-at-R threshold to test.</summary>
public sealed record PyramidTrial
{
    public required double AddAtR { get; init; }
    public required double RiskPips { get; init; }
    public double? TpMultiple { get; init; }
    public required TradeDirection Direction { get; init; }
}

/// <summary>Per-trade outcome for one add-at-R level.</summary>
public sealed record PyramidOutcome
{
    public required double AddAtR { get; init; }
    public required bool Triggered { get; init; }
    public required int AddAtBar { get; init; }
    public required double BaseRMultiple { get; init; }
    public required double PyramidRMultiple { get; init; }
    public required double Improvement { get; init; }
}

/// <summary>Aggregate pyramiding stats across trades for one add-at-R level.</summary>
public sealed record PyramidLevelSummary
{
    public required double AddAtR { get; init; }
    public required int TotalTrades { get; init; }
    public required int Triggered { get; init; }
    public required double TriggerRate { get; init; }
    public required int Improved { get; init; }
    public required double ImprovedRate { get; init; }
    public required int Breakeven { get; init; }
    public required double BreakevenRate { get; init; }
    public required int Worsened { get; init; }
    public required double WorsenedRate { get; init; }
    public required double AvgBaseR { get; init; }
    public required double AvgPyramidR { get; init; }
    public required double AvgImprovement { get; init; }
    public required IReadOnlyList<PyramidOutcome> Outcomes { get; init; }
}

public static class PyramidDiagnosis
{
    /// <summary>
    /// Evaluate pyramiding at the given add-at-R threshold against one trade's excursion path.
    /// Walks the path point-by-point following the ExitReplayer's SL-first-conservative order.
    /// </summary>
    public static PyramidOutcome Evaluate(PyramidTrial trial, IReadOnlyList<PyramidPathPoint> path)
    {
        var dir = trial.Direction == TradeDirection.Long ? 1 : -1;
        var riskPips = trial.RiskPips;
        var addAtPips = trial.AddAtR * riskPips;
        var tpPips = trial.TpMultiple is { } tpMultiple and > 0
            ? dir * tpMultiple * riskPips
            : (double?)null;

        var slPips = -dir * riskPips;

        // --- Compute base R-multiple (no pyramid) ---
        var baseRPips = ComputeBaseExitPips(dir, riskPips, tpPips, slPips, path);
        var baseR = baseRPips / riskPips * dir;

        // --- Detect add trigger ---
        var addTriggered = false;
        var addAtBar = -1;
        for (var i = 0; i < path.Count; i++)
        {
            var point = path[i];
            var signedFavorable = dir > 0 ? point.HiPips : point.LoPips;

            if (dir > 0 ? signedFavorable >= addAtPips : signedFavorable <= -addAtPips)
            {
                addTriggered = true;
                addAtBar = i;
                break;
            }
        }

        if (!addTriggered)
        {
            return new PyramidOutcome
            {
                AddAtR = trial.AddAtR,
                Triggered = false,
                AddAtBar = -1,
                BaseRMultiple = baseR,
                PyramidRMultiple = baseR,
                Improvement = 0,
            };
        }

        // --- Compute pyramid R-multiple ---
        // From the add bar onward:
        // - Original position: SL is now at BE (0 pips from entry)
        // - Add position: enters at addAtPips, SL at 0 (breakeven for original, -addAtR for add)
        // - Combined: 2× size from add bar, combined SL at BE (entry price)
        // Exit detection: combined SL at entry price (0 pips), same TP
        // If exit is at combined SL: original = 0R, add = -addAtR → total = -addAtR R
        // If exit is at TP: original = tpR, add = tpR - addAtR → total = 2*tpR - addAtR
        // If exit is at end-of-data: both exit at close price

        var pyramidRPips = ComputePyramidExitPips(dir, riskPips, tpPips, slPips, addAtPips, addAtBar, path);
        var pyramidR = pyramidRPips / riskPips * dir;

        return new PyramidOutcome
        {
            AddAtR = trial.AddAtR,
            Triggered = true,
            AddAtBar = addAtBar,
            BaseRMultiple = baseR,
            PyramidRMultiple = pyramidR,
            Improvement = pyramidR - baseR,
        };
    }

    /// <summary>
    /// Aggregate per-trade outcomes into a level summary.
    /// </summary>
    public static PyramidLevelSummary Summarize(double addAtR, IReadOnlyList<PyramidOutcome> outcomes)
    {
        var total = outcomes.Count;
        var triggered = outcomes.Count(o => o.Triggered);
        var improved = outcomes.Count(o => o.Triggered && o.Improvement > 0.001);
        var worsened = outcomes.Count(o => o.Triggered && o.Improvement < -0.001);
        var breakeven = outcomes.Count(o => o.Triggered && Math.Abs(o.Improvement) <= 0.001);
        var avgBaseR = outcomes.Average(o => o.BaseRMultiple);
        var avgPyramidR = outcomes.Average(o => o.PyramidRMultiple);
        var avgImprovement = outcomes.Where(o => o.Triggered).Select(o => o.Improvement).DefaultIfEmpty(0).Average();

        // breakeven rate is among triggered, not all trades
        var triggeredCount = outcomes.Count(o => o.Triggered);
        var beRate = triggeredCount > 0 ? (double)breakeven / triggeredCount : 0;

        return new PyramidLevelSummary
        {
            AddAtR = addAtR,
            TotalTrades = total,
            Triggered = triggered,
            TriggerRate = total > 0 ? (double)triggered / total : 0,
            Improved = improved,
            ImprovedRate = total > 0 ? (double)improved / total : 0,
            Breakeven = breakeven,
            BreakevenRate = beRate,
            Worsened = worsened,
            WorsenedRate = total > 0 ? (double)worsened / total : 0,
            AvgBaseR = avgBaseR,
            AvgPyramidR = avgPyramidR,
            AvgImprovement = avgImprovement,
            Outcomes = outcomes,
        };
    }

    /// <summary>Recommended add levels to test: 0.5R through 3.0R.</summary>
    public static readonly double[] DefaultAddLevels = [0.5, 1.0, 1.5, 2.0, 2.5, 3.0];

    // --- private helpers ---

    /// <summary>Compute base (no-pyramid) exit pips: same logic as ExitReplayer but simplified.</summary>
    private static double ComputeBaseExitPips(int dir, double riskPips, double? tpPips, double slPips,
        IReadOnlyList<PyramidPathPoint> path)
    {
        for (var i = 0; i < path.Count; i++)
        {
            var point = path[i];
            var barLo = point.LoPips;
            var barHi = point.HiPips;

            // SL-first-conservative
            var slHit = dir > 0 ? barLo <= slPips : barHi >= slPips;
            if (slHit) return slPips;

            if (tpPips is { } tp)
            {
                var tpHit = dir > 0 ? barHi >= tp : barLo <= tp;
                if (tpHit) return tp;
            }
        }

        // end of data
        return path.Count > 0
            ? (dir > 0 ? path[^1].LoPips : path[^1].HiPips)
            : 0;
    }

    /// <summary>
    /// Walk the path with a pyramid add at addAtBar. After the add, the original SL moves to breakeven
    /// (entry price, 0 pips). The add position has SL at entry price (same BE level). Combined:
    /// - Combined SL at entry: original=0, add=-addAtR → net = -addAtR R
    /// - Combined TP: original=tpR, add=tpR-addAtR → net = 2*tpR - addAtR
    /// - End of data: both exit at close
    /// All R-multiples use the original riskPips as denominator for direct comparison with BaseR.
    /// </summary>
    private static double ComputePyramidExitPips(int dir, double riskPips, double? tpPips, double slPips,
        double addAtPips, int addAtBar, IReadOnlyList<PyramidPathPoint> path)
    {
        var signedAddAtPips = dir * addAtPips;

        // Pre-add bars: standard exit logic (SL-first)
        for (var i = 0; i <= addAtBar; i++)
        {
            var point = path[i];
            var slHit = dir > 0 ? point.LoPips <= slPips : point.HiPips >= slPips;
            if (slHit) return slPips;

            if (tpPips is { } tp)
            {
                var tpHit = dir > 0 ? point.HiPips >= tp : point.LoPips <= tp;
                if (tpHit) return tp;
            }
        }

        // Post-add: combined SL at entry price (0 pips from entry)
        var baseSlPips = 0.0; // combined SL relative to original entry

        for (var i = addAtBar + 1; i < path.Count; i++)
        {
            var point = path[i];

            var combinedSlHit = dir > 0 ? point.LoPips <= baseSlPips : point.HiPips >= baseSlPips;
            if (combinedSlHit)
            {
                // Original exits at 0 pips, add exits at (0 - signedAddAtPips)
                // Total pips = 0 + (0 - signedAddAtPips) = -signedAddAtPips
                return -signedAddAtPips;
            }

            if (tpPips is { } tp)
            {
                var tpHit = dir > 0 ? point.HiPips >= tp : point.LoPips <= tp;
                if (tpHit)
                {
                    // Original exits at tp pips, add exits at (tp - signedAddAtPips)
                    // Total pips = tp + (tp - signedAddAtPips) = 2*tp - signedAddAtPips
                    return 2 * tp - signedAddAtPips;
                }
            }
        }

        // End of data: both exit at close
        if (path.Count == 0) return 0;
        var closePips = dir > 0 ? path[^1].LoPips : path[^1].HiPips;
        return closePips + (closePips - signedAddAtPips);
    }
}

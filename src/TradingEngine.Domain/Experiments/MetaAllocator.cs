namespace TradingEngine.Domain.Experiments;

/// <summary>
/// P6.6 — meta-allocator: monthly re-weighting of enabled cells by contribution score (proxy for
/// rolling OOS P(pass)). Pure config-in/config-out, auditable, no ML. The existing rotation service
/// (win-rate based, RotationMode.PerformanceBased) is the primitive this replaces; avg-R × √frequency
/// is the right objective. Designed as a playbook step that fetches scoreboard data and produces a
/// ranked allocation + park/keep/monitor recommendation.
/// </summary>

public sealed record CellMetrics(
    string StrategyId, string Symbol, string Timeframe,
    double AvgR, int TotalTrades, double TradesPerWeek,
    bool Enabled, bool Parked);

public sealed record CellAllocation(
    string StrategyId, string Symbol, string Timeframe,
    double Weight, double ContributionScore,
    int Rank, string Recommendation);

public sealed record MetaAllocatorOptions(
    int MinTrades = 10,
    double FrequencyWeight = 0.3,
    int TopN = 4,
    double ParkFraction = 0.1);

public sealed record MetaAllocationResult(
    IReadOnlyList<CellAllocation> Allocations,
    double TotalScore,
    int CellsEvaluated,
    int CellsRecommended,
    string Summary);

public static class MetaAllocator
{
    /// <summary>
    /// Compute per-cell allocation weights from scoreboard-style metrics.
    /// Contribution = avgR × (tradesPerWeek ^ frequencyWeight), penalized for low sample size.
    /// Weights are normalized to sum = 1.0. Cells below <paramref name="options"/>.ParkFraction
    /// of the top score are recommended for parking. Top <see cref="MetaAllocatorOptions.TopN"/>
    /// cells are recommended as the active portfolio.
    /// </summary>
    public static MetaAllocationResult Allocate(
        IReadOnlyList<CellMetrics> cells,
        MetaAllocatorOptions? options = null)
    {
        var opts = options ?? new MetaAllocatorOptions();

        if (cells.Count == 0)
        {
            return new MetaAllocationResult(
                [],
                0, 0, 0,
                "No cells provided. Ensure at least one enabled cell with sufficient trade history.");
        }

        var enabled = cells.Where(c => c.Enabled && !c.Parked).ToList();
        if (enabled.Count == 0)
        {
            return new MetaAllocationResult(
                [],
                0, cells.Count, 0,
                "No enabled unparked cells. Park or enable at least one cell before allocating.");
        }

        var scored = enabled
            .Select(c => new
            {
                Cell = c,
                Contribution = ComputeContribution(c, opts),
            })
            .Where(x => !double.IsNaN(x.Contribution) && x.Contribution > 0)
            .OrderByDescending(x => x.Contribution)
            .ToList();

        if (scored.Count == 0)
        {
            return new MetaAllocationResult(
                [],
                0, cells.Count, 0,
                "No cells with positive contribution score. Check trade history and average R.");
        }

        var totalScore = scored.Sum(x => x.Contribution);
        var topScore = scored[0].Contribution;
        var parkThreshold = topScore * opts.ParkFraction;

        var topN = Math.Min(opts.TopN, scored.Count);
        var allocations = scored.Select((x, i) =>
        {
            var weight = totalScore > 0 ? x.Contribution / totalScore : 1.0 / scored.Count;
            var recommendation = i < topN
                ? "keep"
                : x.Contribution < parkThreshold
                    ? "park"
                    : "monitor";

            return new CellAllocation(
                x.Cell.StrategyId,
                x.Cell.Symbol,
                x.Cell.Timeframe,
                Math.Round(weight, 6),
                Math.Round(x.Contribution, 4),
                i + 1,
                recommendation);
        }).ToList();

        var cellsRecommended = allocations.Count(a => a.Recommendation is "keep" or "monitor");
        var topCell = allocations[0];
        var summary = scored.Count == 0
            ? "No cells scored."
            : $"Top cell: {topCell.StrategyId}/{topCell.Symbol}/{topCell.Timeframe} " +
              $"(score={topCell.ContributionScore:F4}, weight={topCell.Weight:P1}). " +
              $"Keep {allocations.Count(a => a.Recommendation == "keep")}, " +
              $"monitor {allocations.Count(a => a.Recommendation == "monitor")}, " +
              $"park {allocations.Count(a => a.Recommendation == "park")}.";

        return new MetaAllocationResult(
            allocations.AsReadOnly(),
            Math.Round(totalScore, 4),
            cells.Count,
            cellsRecommended,
            summary);
    }

    /// <summary>
    /// Contribution score per cell: avgR weighted by √frequency. Low-sample cells are penalized
    /// by a confidence factor that ramps from 0.5 at 1 trade to 1.0 at <paramref name="opts"/>.MinTrades.
    /// AvgR is floored at 0 (negative edge cells get scored by frequency alone, then ranked low).
    /// </summary>
    private static double ComputeContribution(CellMetrics cell, MetaAllocatorOptions opts)
    {
        var avgR = Math.Max(0, cell.AvgR);
        var frequencyScore = Math.Pow(cell.TradesPerWeek, opts.FrequencyWeight);

        var confidence = Math.Min(1.0, cell.TotalTrades / (double)opts.MinTrades);
        if (confidence < 0.5) confidence = 0.5; // floor: even 1-trade cells get some weight

        return avgR * frequencyScore * confidence;
    }
}

using TradingEngine.Domain.Experiments;

namespace TradingEngine.Host.Experiments;

public static class VariantScorer
{
    public static FoldScore ScoreFold(
        IReadOnlyList<TradeResult> trades,
        IReadOnlyList<EquitySnapshot> equitySnapshots,
        PropFirmRuleSet rules,
        int foldIndex,
        string foldRole,
        IPassProbabilityEstimator passEstimator)
    {
        var passProb = ComputePassProbability(trades, equitySnapshots, rules, passEstimator);
        var expectancyR = trades.Count > 0 ? trades.Average(t => t.RMultiple) : 0.0;
        var maxDD = ComputeMaxDrawdown(equitySnapshots);
        var composite = ComputeComposite(passProb, expectancyR, maxDD, null);

        return new FoldScore(
            foldIndex,
            foldRole,
            composite,
            passProb,
            expectancyR,
            maxDD,
            trades.Count);
    }

    public static VariantScore ScoreVariant(
        string label,
        IReadOnlyList<FoldScore> folds,
        ScoringWeights weights)
    {
        var testFolds = folds.Where(f => f.FoldRole == "Test").ToList();
        var allFolds = folds.ToList();

        if (testFolds.Count == 0)
            testFolds = allFolds;

        var avgPassProb = testFolds.Average(f => f.PassProbability);
        var avgExpectancyR = testFolds.Average(f => f.ExpectancyR);
        var avgMaxDD = testFolds.Average(f => f.MaxDrawdownPercent);
        var totalTrades = testFolds.Sum(f => f.TotalTrades);

        var foldConsistency = ComputeFoldConsistency(allFolds);

        var composite =
            avgPassProb * weights.PassProbability +
            NormalizeExpectancy(avgExpectancyR) * weights.ExpectancyR +
            (1.0 - Math.Min(avgMaxDD / 100.0, 1.0)) * weights.MaxDrawdown +
            foldConsistency * weights.FoldConsistency;

        return new VariantScore(
            label,
            composite,
            avgPassProb,
            avgExpectancyR,
            avgMaxDD,
            foldConsistency,
            totalTrades,
            allFolds);
    }

    private static double ComputePassProbability(
        IReadOnlyList<TradeResult> trades,
        IReadOnlyList<EquitySnapshot> equitySnapshots,
        PropFirmRuleSet rules,
        IPassProbabilityEstimator passEstimator)
    {
        var dailyPnL = ComputeDailyPnL(trades, equitySnapshots);
        if (dailyPnL.Count == 0) return 0.0;

        var lastEquity = equitySnapshots.Count > 0
            ? equitySnapshots[^1].Equity
            : 0m;

        var input = new PassProbabilityInput
        {
            CurrentEquity = lastEquity,
            InitialBalance = equitySnapshots.Count > 0 ? equitySnapshots[0].Equity : 100_000m,
            ProfitTargetPercent = rules.ProfitTargetPercent,
            MaxDailyLossPercent = rules.MaxDailyLossPercent,
            MaxTotalLossPercent = rules.MaxTotalLossPercent,
            DaysRemaining = Math.Max(1, 20 - dailyPnL.Count),
            HistoricalDailyPnL = dailyPnL,
            MonteCarloRuns = 5_000,
        };

        var estimate = passEstimator.Estimate(input);
        return estimate.ProbabilityOfPass;
    }

    private static IReadOnlyList<decimal> ComputeDailyPnL(
        IReadOnlyList<TradeResult> trades,
        IReadOnlyList<EquitySnapshot> equitySnapshots)
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

    private static double ComputeMaxDrawdown(IReadOnlyList<EquitySnapshot> equitySnapshots)
    {
        if (equitySnapshots.Count == 0) return 0;

        var peak = equitySnapshots[0].Equity;
        var maxDD = 0m;

        foreach (var snap in equitySnapshots)
        {
            if (snap.Equity > peak) peak = snap.Equity;
            var dd = peak > 0 ? (peak - snap.Equity) / peak * 100m : 0m;
            if (dd > maxDD) maxDD = dd;
        }

        return (double)maxDD;
    }

    private static double ComputeComposite(
        double passProb, double expectancyR, double maxDD, ScoringWeights? weights)
    {
        var w = weights ?? new ScoringWeights();
        return passProb * w.PassProbability +
               NormalizeExpectancy(expectancyR) * w.ExpectancyR +
               (1.0 - Math.Min(maxDD / 100.0, 1.0)) * w.MaxDrawdown;
    }

    private static double NormalizeExpectancy(double r)
        => Math.Min(Math.Max((r + 1.0) / 3.0, 0.0), 1.0);

    private static double ComputeFoldConsistency(IReadOnlyList<FoldScore> folds)
    {
        if (folds.Count < 2) return 1.0;

        var composites = folds.Select(f => f.Composite).ToList();
        var mean = composites.Average();
        if (mean == 0) return 1.0;

        var variance = composites.Sum(c => (c - mean) * (c - mean)) / composites.Count;
        var stdDev = Math.Sqrt(variance);
        return Math.Clamp(1.0 - stdDev / mean, 0.0, 1.0);
    }
}

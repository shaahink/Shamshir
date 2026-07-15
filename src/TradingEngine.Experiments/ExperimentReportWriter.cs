using System.Text;

namespace TradingEngine.Experiments;

public static class ExperimentReportWriter
{
    public static async Task WriteAsync(
        ExperimentSpec spec,
        Guid experimentId,
        IReadOnlyList<VariantScore> scores,
        string solutionRoot,
        CancellationToken ct)
    {
        var shortId = experimentId.ToString("N")[..8];
        var dir = Path.Combine(solutionRoot, "docs", "experiments", $"{spec.Name}-{shortId}");
        Directory.CreateDirectory(dir);

        var md = BuildMarkdown(spec, experimentId, scores);
        await File.WriteAllTextAsync(Path.Combine(dir, "REPORT.md"), md, ct);

        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(new
        {
            experimentId = experimentId.ToString(),
            spec.Name,
            spec.Hypothesis,
            scores = scores.Select(s => new
            {
                s.Label,
                s.Composite,
                s.PassProbability,
                s.ExpectancyR,
                s.MaxDrawdownPercent,
                s.FoldConsistency,
                s.TotalTrades,
                Folds = s.Folds.Select(f => new
                {
                    f.FoldIndex,
                    f.FoldRole,
                    f.Composite,
                    f.PassProbability,
                    f.ExpectancyR,
                    f.MaxDrawdownPercent,
                    f.TotalTrades,
                }),
            }),
        }, jsonOptions);
        await File.WriteAllTextAsync(Path.Combine(dir, "report.json"), json, ct);
    }

    private static string BuildMarkdown(
        ExperimentSpec spec,
        Guid experimentId,
        IReadOnlyList<VariantScore> scores)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Experiment: {spec.Name}");
        sb.AppendLine();
        sb.AppendLine($"**ID**: `{experimentId}`");
        sb.AppendLine($"**Hypothesis**: {spec.Hypothesis}");
        sb.AppendLine($"**Symbols**: {string.Join(", ", spec.Symbols)}");
        sb.AppendLine($"**Timeframes**: {string.Join(", ", spec.Timeframes)}");
        sb.AppendLine($"**Strategies**: {string.Join(", ", spec.Strategies)}");
        sb.AppendLine($"**Range**: {spec.From:yyyy-MM-dd} → {spec.To:yyyy-MM-dd}");
        sb.AppendLine(spec.WalkForward is not null
            ? $"**Walk-Forward**: {spec.WalkForward.Folds} folds, {spec.WalkForward.TrainFraction:P0} train"
            : "**Walk-Forward**: None (single full-range)");
        sb.AppendLine();

        var sorted = scores.OrderByDescending(s => s.Composite).ToList();

        sb.AppendLine("## Results");
        sb.AppendLine();
        sb.AppendLine("| # | Variant | Composite | Pass Probability | Expectancy R | Max DD% | Fold Consistency | Trades |");
        sb.AppendLine("|---|---------|-----------|------------------|-------------|---------|-----------------|--------|");

        for (var i = 0; i < sorted.Count; i++)
        {
            var s = sorted[i];
            sb.AppendLine($"| {i + 1} | {s.Label} | {s.Composite:F3} | {s.PassProbability:P1} | {s.ExpectancyR:F2}R | {s.MaxDrawdownPercent:F1}% | {s.FoldConsistency:F2} | {s.TotalTrades} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Per-Fold Breakdown");
        sb.AppendLine();

        foreach (var variant in sorted)
        {
            sb.AppendLine($"### {variant.Label}");
            sb.AppendLine();
            sb.AppendLine("| Fold | Role | Composite | Pass Prob | Exp. R | Max DD% | Trades |");
            sb.AppendLine("|------|------|-----------|-----------|---------|---------|--------|");

            foreach (var fold in variant.Folds)
            {
                sb.AppendLine($"| {fold.FoldIndex} | {fold.FoldRole} | {fold.Composite:F3} | {fold.PassProbability:P1} | {fold.ExpectancyR:F2}R | {fold.MaxDrawdownPercent:F1}% | {fold.TotalTrades} |");
            }

            var trainFolds = variant.Folds.Where(f => f.FoldRole == "Train").ToList();
            var testFolds = variant.Folds.Where(f => f.FoldRole == "Test").ToList();
            if (trainFolds.Count > 0 && testFolds.Count > 0)
            {
                var trainComp = trainFolds.Average(f => f.Composite);
                var testComp = testFolds.Average(f => f.Composite);
                var gap = testComp - trainComp;
                sb.AppendLine();
                sb.AppendLine($"**Train Avg**: {trainComp:F3} | **Test Avg**: {testComp:F3} | **Gap**: {gap:+0.000;-0.000}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("## Verdicts");
        sb.AppendLine();

        foreach (var variant in sorted)
        {
            var testFolds = variant.Folds.Where(f => f.FoldRole == "Test").ToList();
            if (testFolds.Count == 0) testFolds = variant.Folds.ToList();

            var winning = testFolds.Count(f => f.Composite > sorted.First().Composite * 0.9);
            var verdict = winning >= testFolds.Count
                ? $"**{variant.Label}**: CONSISTENT — wins in {winning}/{testFolds.Count} test folds"
                : $"**{variant.Label}**: INCONSISTENT — wins in {winning}/{testFolds.Count} test folds, rejected";
            sb.AppendLine(verdict);
        }

        return sb.ToString();
    }
}

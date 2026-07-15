using System.Text.Json;

namespace TradingEngine.Domain.Experiments;

public record ExperimentSpec(
    string Name,
    string Hypothesis,
    string[] Symbols,
    string[] Timeframes,
    string[] Strategies,
    DateOnly From,
    DateOnly To,
    WalkForwardSpec? WalkForward = null,
    VariantSpec[] Variants = null!,
    ScoringWeights? Scoring = null,
    int MaxRuns = 64)
{
    public VariantSpec[] Variants { get; init; } = Variants ?? [];
    public ScoringWeights Scoring { get; init; } = Scoring ?? new();
}

public record WalkForwardSpec(int Folds = 4, double TrainFraction = 0.7)
{
    public string[]? Strategies { get; init; }
    public string[]? Symbols { get; init; }
    public string[]? Timeframes { get; init; }
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public Dictionary<string, decimal[]>? ParamGrid { get; init; }
    public decimal Balance { get; init; } = 100_000m;

    // R3.2: without these, every train/test window silently reverts to the strategy's default
    // pack/risk profile — walk-forwarding a variant that beat baseline on a pack or risk-profile
    // swap would validate the WRONG config. Optional so existing default-config walk-forwards
    // (no pack/risk override) are unaffected.
    public string? PackId { get; init; }
    public string? RiskProfileId { get; init; }
}

public record VariantSpec(string Label)
{
    public Dictionary<string, JsonElement>? Overrides { get; init; }
}

public record ScoringWeights(
    double PassProbability = 0.4,
    double ExpectancyR = 0.3,
    double MaxDrawdown = 0.2,
    double FoldConsistency = 0.1);

public record VariantScore(
    string Label,
    double Composite,
    double PassProbability,
    double ExpectancyR,
    double MaxDrawdownPercent,
    double FoldConsistency,
    int TotalTrades,
    IReadOnlyList<FoldScore> Folds);

public record FoldScore(
    int FoldIndex,
    string FoldRole,
    double Composite,
    double PassProbability,
    double ExpectancyR,
    double MaxDrawdownPercent,
    int TotalTrades);

namespace TradingEngine.Domain;

public record PassProbabilityEstimate
{
    public double ProbabilityOfPass { get; init; }
    public double ProbabilityOfDailyBreach { get; init; }
    public double ProbabilityOfMaxBreach { get; init; }
    public int ExpectedDaysToTarget { get; init; }
    public decimal ProjectedFinalEquity { get; init; }
    public string Recommendation { get; init; } = "";
}

public record PassProbabilityInput
{
    public decimal CurrentEquity { get; init; }
    public decimal InitialBalance { get; init; }
    public double ProfitTargetPercent { get; init; }
    public double MaxDailyLossPercent { get; init; }
    public double MaxTotalLossPercent { get; init; }
    public int DaysRemaining { get; init; }
    public IReadOnlyList<decimal> HistoricalDailyPnL { get; init; } = [];
    public int MonteCarloRuns { get; init; } = 10_000;
    public DailyDdBase DailyDdBase { get; init; } = DailyDdBase.InitialBalance;
}

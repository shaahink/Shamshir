namespace TradingEngine.Domain;

public record StrategyRotationOptions
{
    public RotationMode Mode { get; init; } = RotationMode.Disabled;
    public int EvaluationWindowDays { get; init; } = 30;
    public double MinWinRateToKeepActive { get; init; } = 0.35;
    public double MinProfitFactorToKeepActive { get; init; } = 0.8;
    public int MinTradesForEvaluation { get; init; } = 10;
}

public enum RotationMode { Disabled, PerformanceBased, RegimeBased, Combined }

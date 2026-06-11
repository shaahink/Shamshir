namespace TradingEngine.Domain;

public record GracePeriodOptions
{
    public bool Enabled { get; init; }
    public int MaxGraceDaysPerMonth { get; init; } = 1;
    public double MaxDDForGrace { get; init; } = 0.02;
}

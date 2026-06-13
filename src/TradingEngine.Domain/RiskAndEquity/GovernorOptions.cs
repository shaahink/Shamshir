namespace TradingEngine.Domain;

public record GovernorOptions
{
    public bool Enabled { get; init; } = true;
    public double[] LossBandFractions { get; init; } = [0.4, 0.6];
    public double[] LossBandMultipliers { get; init; } = [0.5, 0.0]; // Reduced=×0.5, SoftStop=×0.0
    public int StreakReduceAt { get; init; } = 3;
    public double StreakMultiplier { get; init; } = 0.5;
    public int StreakPauseAt { get; init; } = 5;
    public int CoolingOffBars { get; init; } = 24;
    public bool ProfitLockEnabled { get; init; } = true;
    public double ProfitLockFraction { get; init; } = 0.6;
}

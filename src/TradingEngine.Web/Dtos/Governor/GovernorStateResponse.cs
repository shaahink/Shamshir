namespace TradingEngine.Web.Dtos.Governor;

public sealed record GovernorStateResponse
{
    public required string State { get; init; }
    public decimal SizeMultiplier { get; init; }
    public int ConsecutiveLosses { get; init; }
    public double DayNetPnLFraction { get; init; }
    public double DistanceToDailyLimitFraction { get; init; }
    public string? Reason { get; init; }
}

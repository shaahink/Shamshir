namespace TradingEngine.Domain;

public record ComplianceSummary
{
    public bool IsInChallenge { get; init; }
    public decimal CurrentEquity { get; init; }
    public decimal TargetEquity { get; init; }
    public decimal MaxDrawdownFloor { get; init; }
    public double EstimatedPassProbability { get; init; }
    public int TradingDaysMet { get; init; }
    public int TradingDaysRequired { get; init; }
    public string Status { get; init; } = "";
}

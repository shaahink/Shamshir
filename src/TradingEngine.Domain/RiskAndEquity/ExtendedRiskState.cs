namespace TradingEngine.Domain;

public record ExtendedRiskState
{
    public bool TradingAllowed { get; init; }
    public bool InProtectionMode { get; init; }
    public string? ProtectionReason { get; init; }
    public decimal DailyDrawdownUsed { get; init; }
    public decimal WeeklyDrawdownUsed { get; init; }
    public decimal MonthlyDrawdownUsed { get; init; }
    public decimal MaxDrawdownUsed { get; init; }
    public decimal DrawdownVelocity { get; init; }
    public bool IsDrawdownAccelerating { get; init; }
    public CurrencyExposureSnapshot CurrencyExposure { get; init; } = new();
    public DateTime? ProtectionUntilUtc { get; init; }
    public decimal DailyDrawdownLimit { get; init; }
    public decimal MaxDrawdownLimit { get; init; }
}

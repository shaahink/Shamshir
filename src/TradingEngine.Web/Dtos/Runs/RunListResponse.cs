namespace TradingEngine.Web.Dtos.Runs;

public sealed record RunListResponse
{
    public required string RunId { get; init; }
    public required string Status { get; init; }
    public required string Symbol { get; init; }
    public required string Period { get; init; }
    public string Symbols { get; init; } = "[]";
    public string Periods { get; init; } = "[]";
    public DateTime StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public decimal NetProfit { get; init; }
    public decimal GrossPnL { get; init; }
    public decimal CommissionTotal { get; init; }
    public decimal SwapTotal { get; init; }
    public decimal MaxDrawdownPct { get; init; }
    public int TotalTrades { get; init; }
    public int WinningTrades { get; init; }
    public double WinRatePct { get; init; }
    public string? ErrorMessage { get; init; }
    public string? WarningsJson { get; init; }
    public string? Venue { get; init; }
    public string? RiskProfileId { get; init; }
    public string? ParentRunId { get; init; }
    public string? ComparePairId { get; init; }
    public int? QueuePosition { get; init; }
    public string? PersistedStatus { get; init; }
}

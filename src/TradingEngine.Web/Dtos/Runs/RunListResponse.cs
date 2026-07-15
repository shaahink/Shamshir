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

    // X2: richer runs table — duration, note, latest SetupScore composite, and the distinct
    // strategy ids derived from the persisted run plan.
    public long WallElapsedMs { get; init; }
    public string? Notes { get; init; }
    public double? Score { get; init; }
    public string? Strategies { get; init; }

    /// <summary>Projection-only carrier for deriving <see cref="Strategies"/>; never serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RunPlanJson { get; init; }
}

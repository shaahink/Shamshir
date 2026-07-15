namespace TradingEngine.Web.Dtos.Trades;

public sealed record TradeDetailResponse
{
    public required Guid Id { get; init; }
    public required Guid PositionId { get; init; }
    public Guid OrderId { get; init; }
    public required string Symbol { get; init; }
    public required string Direction { get; init; }
    public decimal Lots { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public decimal StopLoss { get; init; }
    public decimal? TakeProfit { get; init; }
    public DateTime OpenedAtUtc { get; init; }
    public DateTime ClosedAtUtc { get; init; }
    public decimal GrossPnLAmount { get; init; }
    public decimal CommissionAmount { get; init; }
    public decimal SwapAmount { get; init; }
    public decimal NetPnLAmount { get; init; }
    public double PnLPips { get; init; }
    public double RMultiple { get; init; }
    public double MaxAdverseExcursion { get; init; }
    public double MaxFavorableExcursion { get; init; }
    public double? MaeR { get; init; }
    public double? MfeR { get; init; }
    public required string ExitReason { get; init; }
    public required string StrategyId { get; init; }
    public double DurationSeconds { get; init; }

    /// <summary>The run's timeframe (e.g. "H1"), so the trade-detail chart queries bars at the right
    /// resolution instead of guessing.</summary>
    public string Timeframe { get; init; } = "H1";
    public string? EntryReason { get; init; }
    public string? EntryRegime { get; init; }
    public string? EntrySnapshotJson { get; init; }
    public string? ExitDetailJson { get; init; }

    // X3: prev/next navigation within the trade's run (ordered by OpenedAtUtc, then Id).
    public string? RunId { get; init; }
    public Guid? PrevTradeId { get; init; }
    public Guid? NextTradeId { get; init; }
    public int TradeIndex { get; init; }     // 1-based position within the run
    public int TradeCount { get; init; }
}

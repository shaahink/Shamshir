namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class TradeResultEntity : IAuditableEntity
{
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public Guid Id { get; set; }
    public Guid PositionId { get; set; }
    // Venue-facing clientOrderId (originating order). Equals the cBot ledger's clientOrderId — the
    // exact per-trade reconciliation join key. Distinct from the engine-internal PositionId.
    public Guid OrderId { get; set; }
    public string Symbol { get; set; } = "";
    public string Direction { get; set; } = "";
    public decimal Lots { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public DateTime OpenedAtUtc { get; set; }
    public DateTime ClosedAtUtc { get; set; }
    public decimal GrossPnLAmount { get; set; }
    public string GrossPnLCurrency { get; set; } = "";
    public decimal CommissionAmount { get; set; }
    public string CommissionCurrency { get; set; } = "";
    public decimal SwapAmount { get; set; }
    public string SwapCurrency { get; set; } = "";
    public decimal NetPnLAmount { get; set; }
    public string NetPnLCurrency { get; set; } = "";
    public double PnLPips { get; set; }
    public double RMultiple { get; set; }
    public double MaxAdverseExcursion { get; set; }
    public double MaxFavorableExcursion { get; set; }
    public string ExitReason { get; set; } = "";
    public string StrategyId { get; set; } = "";
    public string RiskProfileId { get; set; } = "";
    public string Mode { get; set; } = "";
    public string OrderEntryMethod { get; set; } = "";
    public double DurationSeconds { get; set; }
    public string? RunId { get; set; }
    public string? EntryReason { get; set; }
    public string? EntryRegime { get; set; }
    public string? EntrySnapshotJson { get; set; }
    public string? ExitDetailJson { get; set; }
    public decimal? InitialStopLoss { get; set; }
    // P4.5.6: the decision bar's timeframe — enables scoreboard to filter trades by TF
    // (previously impossible: TradeResultEntity had no TF column, so H1+M15 trades merged).
    public string? EntryTimeframe { get; set; }
}

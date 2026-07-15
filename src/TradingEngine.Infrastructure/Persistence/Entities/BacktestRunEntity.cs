namespace TradingEngine.Infrastructure.Persistence.Entities;

public sealed class BacktestRunEntity : IAuditableEntity
{
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public string RunId { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }

    public string Status { get; set; } = "";
    public int? QueuePosition { get; set; }

    public string Symbol { get; set; } = "";
    public string Period { get; set; } = "";
    public string Symbols { get; set; } = "[]";
    public string Periods { get; set; } = "[]";
    public DateTime BacktestFrom { get; set; }
    public DateTime BacktestTo { get; set; }
    public decimal InitialBalance { get; set; }
    public string AlgoHash { get; set; } = "";
    public string StrategyParamsJson { get; set; } = "{}";
    public string? EffectiveConfigJson { get; set; }
    public decimal NetProfit { get; set; }
    public decimal GrossPnL { get; set; }
    public decimal CommissionTotal { get; set; }
    public decimal SwapTotal { get; set; }
    public decimal MaxDrawdownPct { get; set; }
    public int TotalTrades { get; set; }
    public int WinningTrades { get; set; }
    public double WinRatePct { get; set; }
    public int ExitCode { get; set; }
    public string? ErrorMessage { get; set; }

    // P0.2 (F5, Q5): teardown/persistence anomalies attached to a run that still produced a complete
    // result. Populated => status is `completed-with-warnings`, never `failed`. ErrorMessage stays null
    // (a warning is not a failure). JSON array of { code, detail, atUtc }.
    public string? WarningsJson { get; set; }

    public string? ReportJsonPath { get; set; }
    public string? DatasetId { get; set; }
    public string? ConfigSetId { get; set; }
    public int Seed { get; set; }
    public string? ParentRunId { get; set; }

    // P5.1 (F16): shared key for compare-both pairs (tape parent + cTrader child). Null for solo runs.
    public string? ComparePairId { get; set; }

    // iter-strategy-system P2 (D5): persist the run's full selection so the report shows exactly what was
    // run. RunPlanJson is the array of rows (strategy, symbol, timeframe, pack); the rest are the run-level
    // choices the builder sends (D4).
    public string RunPlanJson { get; set; } = "[]";
    public string? Venue { get; set; }
    public string? RiskProfileId { get; set; }
    public bool GovernorEnabled { get; set; } = true;
    public bool RegimeEnabled { get; set; } = true;
    public double CommissionPerMillion { get; set; }
    public double SpreadPips { get; set; }

    // P9: run profiling
    public long WallElapsedMs { get; set; }
    public double BarsPerSec { get; set; }
    public int TotalBars { get; set; }

    // P4.1 (F11) / P7.1: exploration mode + excursion recording flags persisted so the
    // run-report exploration banner survives run completion.
    public bool ExplorationMode { get; set; }
    public bool RecordExcursions { get; set; }
}

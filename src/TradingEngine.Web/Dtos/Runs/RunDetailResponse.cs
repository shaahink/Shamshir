namespace TradingEngine.Web.Dtos.Runs;

public sealed record RunDetailResponse
{
    public required string RunId { get; init; }
    public required string Status { get; init; }
    public required string Symbol { get; init; }
    public required string Period { get; init; }
    public string Symbols { get; init; } = "[]";
    public string Periods { get; init; } = "[]";
    public DateTime StartedAtUtc { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public DateTime BacktestFrom { get; init; }
    public DateTime BacktestTo { get; init; }
    public decimal InitialBalance { get; init; }
    public decimal NetProfit { get; init; }
    public decimal GrossPnL { get; init; }
    public decimal CommissionTotal { get; init; }
    public decimal SwapTotal { get; init; }
    public decimal MaxDrawdownPct { get; init; }
    public int TotalTrades { get; init; }
    public int WinningTrades { get; init; }
    public double WinRatePct { get; init; }
    public string? ErrorMessage { get; init; }
    public int ExitCode { get; init; }

    // P0.2 (F5, Q5): teardown/persistence warnings on a `completed-with-warnings` run.
    public string? WarningsJson { get; init; }

    public string? EffectiveConfigJson { get; init; }
    public string? ReportJsonPath { get; init; }

    // iter-strategy-system P2 (D5): the persisted run selection, surfaced for the report.
    public string RunPlanJson { get; init; } = "[]";
    public string? Venue { get; init; }
    public string? RiskProfileId { get; init; }
    public bool GovernorEnabled { get; init; } = true;
    public bool RegimeEnabled { get; init; } = true;
    public double CommissionPerMillion { get; init; }
    public double SpreadPips { get; init; }
    public long WallElapsedMs { get; init; }
    public double BarsPerSec { get; init; }
    public int TotalBars { get; init; }
    public string? ExitResolution { get; init; }

    // P4.1 (F11): whether this run used the one-click exploration preset.
    public bool ExplorationMode { get; init; }

    // P4.1 (F11): whether excursion paths were recorded for this run.
    public bool RecordExcursions { get; init; }

    // P5.1 (F16): parent and compare-pair linkage for child runs and compare-both grouping.
    public string? ParentRunId { get; init; }
    public string? ComparePairId { get; init; }

    // X2: owner's note, editable from the Runs page and the report page.
    public string? Notes { get; init; }
}

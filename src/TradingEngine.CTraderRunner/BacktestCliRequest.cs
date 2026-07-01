namespace TradingEngine.CTraderRunner;

public sealed record BacktestCliRequest
{
    public required string AlgoPath { get; init; }
    public required string Symbol { get; init; }
    public required string Period { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public required string CtId { get; init; }
    public required string PwdFile { get; init; }
    public required string Account { get; init; }
    public required int DataPort { get; init; }
    public required int CommandPort { get; init; }

    public decimal Balance { get; init; } = 100_000m;
    public decimal CommissionPerMillion { get; init; } = 30m;
    public decimal SpreadPips { get; init; } = 1m;
    public string DataMode { get; init; } = "m1";

    public IReadOnlyList<string> Symbols { get; init; } = [];
    public IReadOnlyList<string> Periods { get; init; } = [];
    public bool FullAccess { get; init; } = true;

    /// <summary>Pass <c>--Diagnostics=true</c> so the cBot emits its per-bar round-trip + tick-publish
    /// timing (CBOT|TIMING) in OnStop. Measurement-only — does NOT enable Verbose, so backtest behaviour
    /// is unchanged.</summary>
    public bool Diagnostics { get; init; } = false;

    public string? ReportJsonPath { get; init; }
    public string? DataDir { get; init; }
    public string? DataFile { get; init; }

    /// <summary>Directory the cBot writes its own report.json + events.json into (passed as the
    /// cBot --ReportPath parameter). Our resilient venue ledger, replacing cTrader's --report-json.</summary>
    public string? ReportDir { get; init; }
}

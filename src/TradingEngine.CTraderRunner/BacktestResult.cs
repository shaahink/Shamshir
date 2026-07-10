namespace TradingEngine.CTraderRunner;

public sealed record BacktestResult
{
    public required string RunId { get; init; }
    public int ExitCode { get; init; }
    public bool Success => ExitCode == 0;
    public decimal NetProfit { get; init; }
    public decimal MaxDrawdownPct { get; init; }
    public int TotalTrades { get; init; }
    public int WinningTrades { get; init; }
    public double WinRatePct { get; init; }
    public string AlgoHash { get; init; } = "";
    public string? ErrorMessage { get; init; }

    // P0.2 (F5, Q5): teardown/persistence anomalies that occurred AFTER the engine produced a complete
    // result. Populated => the run is `completed-with-warnings`, not `failed`. JSON array of warnings.
    public string? WarningsJson { get; init; }

    public string? ReportJsonPath { get; init; }
    public long WallElapsedMs { get; init; }
    public double BarsPerSec { get; init; }
    public int TotalBars { get; init; }
}

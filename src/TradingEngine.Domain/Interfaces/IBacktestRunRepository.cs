namespace TradingEngine.Domain;

public sealed record BacktestRunSummary(
    string RunId,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    string Symbol,
    string Period,
    string Symbols,
    string Periods,
    DateTime BacktestFrom,
    DateTime BacktestTo,
    decimal InitialBalance,
    string AlgoHash,
    string StrategyParamsJson,
    string? EffectiveConfigJson,
    decimal NetProfit,
    decimal GrossPnL,
    decimal CommissionTotal,
    decimal SwapTotal,
    decimal MaxDrawdownPct,
    int TotalTrades,
    int WinningTrades,
    double WinRatePct,
    int ExitCode,
    string? ErrorMessage,
    string? ReportJsonPath = null,
    string? DatasetId = null,
    string? ConfigSetId = null,
    int Seed = 0,
    string? ParentRunId = null,
    // iter-strategy-system P2 (D5): the persisted run selection.
    string RunPlanJson = "[]",
    string? Venue = null,
    string? RiskProfileId = null,
    bool GovernorEnabled = true,
    bool RegimeEnabled = true,
    double CommissionPerMillion = 0,
    double SpreadPips = 0,
    long WallElapsedMs = 0,
    double BarsPerSec = 0,
    int TotalBars = 0,
    // P0.2 (F5, Q5): teardown/persistence warnings on a run that still produced a complete result.
    // Populated => `completed-with-warnings`; ErrorMessage stays null.
    string? WarningsJson = null,
    // P5.1 (F16): shared key for compare-both pairs. Null for solo runs.
    string? ComparePairId = null);

public interface IBacktestRunRepository
{
    Task SaveAsync(BacktestRunSummary run, CancellationToken ct);
    Task UpdateAsync(BacktestRunSummary run, CancellationToken ct);
    Task<IReadOnlyList<BacktestRunSummary>> GetAllAsync(CancellationToken ct);
    Task<BacktestRunSummary?> GetByIdAsync(string runId, CancellationToken ct);
    Task DeleteAsync(string runId, CancellationToken ct);
    Task<int> DeleteRunsAsync(IReadOnlyCollection<string> runIds, CancellationToken ct);
}

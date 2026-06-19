namespace TradingEngine.Domain;

public sealed record BacktestRunSummary(
    string RunId,
    DateTime StartedAtUtc,
    DateTime CompletedAtUtc,
    string Symbol,
    string Period,
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
    string? ReportJsonPath = null);

public interface IBacktestRunRepository
{
    Task SaveAsync(BacktestRunSummary run, CancellationToken ct);
    Task UpdateAsync(BacktestRunSummary run, CancellationToken ct);
    Task<IReadOnlyList<BacktestRunSummary>> GetAllAsync(CancellationToken ct);
    Task<BacktestRunSummary?> GetByIdAsync(string runId, CancellationToken ct);
}

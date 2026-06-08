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
    decimal NetProfit,
    decimal MaxDrawdownPct,
    int TotalTrades,
    int WinningTrades,
    double WinRatePct,
    int ExitCode,
    string? ErrorMessage);

public interface IBacktestRunRepository
{
    Task SaveAsync(BacktestRunSummary run, CancellationToken ct);
    Task UpdateAsync(BacktestRunSummary run, CancellationToken ct);
    Task<IReadOnlyList<BacktestRunSummary>> GetAllAsync(CancellationToken ct);
    Task<BacktestRunSummary?> GetByIdAsync(string runId, CancellationToken ct);
}

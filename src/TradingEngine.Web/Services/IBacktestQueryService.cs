namespace TradingEngine.Web.Services;

public sealed record BacktestRunView(
    string RunId,
    DateTime StartedAt,
    string Status,
    string Symbol,
    string Period,
    DateTime BacktestFrom,
    DateTime BacktestTo,
    decimal InitialBalance,
    decimal NetProfit,
    decimal MaxDrawdownPct,
    int TotalTrades,
    int WinningTrades,
    double WinRatePct,
    string AlgoHash,
    string? Error);

public sealed record EquityPoint(DateTime TimestampUtc, decimal Equity, decimal Balance);

public interface IBacktestQueryService
{
    Task<IReadOnlyList<BacktestRunView>> GetAllRunsAsync(CancellationToken ct);
    Task<BacktestRunView?> GetRunAsync(string runId, CancellationToken ct);
}

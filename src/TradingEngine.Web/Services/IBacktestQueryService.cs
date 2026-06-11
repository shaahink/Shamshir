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

public sealed record StrategyPerformance(
    string StrategyId,
    int TotalBarsEvaluated,
    int SignalsFired,
    int TradesOpened,
    int Wins,
    int Losses,
    double WinRatePct,
    IReadOnlyList<(string Reason, int Count)> TopRejections);

public interface IBacktestQueryService
{
    Task<IReadOnlyList<BacktestRunView>> GetAllRunsAsync(CancellationToken ct);
    Task<BacktestRunView?> GetRunAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<StrategyPerformance>> GetStrategyBreakdownAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<EquityPoint>> GetEquityAsync(DateTime? from = null, DateTime? to = null, CancellationToken ct = default);
}

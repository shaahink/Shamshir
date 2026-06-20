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
    string? Error,
    string? EffectiveConfigJson = null);

public sealed record EquityPoint(DateTime TimestampUtc, decimal Equity, decimal Balance);

// iter-37 T3: a NAMED record (not a ValueTuple) so System.Text.Json emits {"reason","count"} — a tuple
// serialized as {"item1","item2"}, which the SPA read as reason/count → "undefined (undefined)".
public sealed record NoSignalReason(string Reason, int Count);

public sealed record StrategyPerformance(
    string StrategyId,
    int TotalBarsEvaluated,
    int SignalsFired,
    int TradesOpened,
    int Wins,
    int Losses,
    double WinRatePct,
    IReadOnlyList<NoSignalReason> TopRejections);

public interface IBacktestQueryService
{
    Task<IReadOnlyList<BacktestRunView>> GetAllRunsAsync(CancellationToken ct);
    Task<BacktestRunView?> GetRunAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<StrategyPerformance>> GetStrategyBreakdownAsync(string runId, CancellationToken ct);
    Task<IReadOnlyList<EquityPoint>> GetEquityAsync(DateTime? from = null, DateTime? to = null, CancellationToken ct = default);
}

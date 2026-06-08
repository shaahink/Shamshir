using TradingEngine.Domain;

namespace TradingEngine.Web.Services;

public sealed class BacktestQueryService : IBacktestQueryService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public BacktestQueryService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<IReadOnlyList<BacktestRunView>> GetAllRunsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
        var summaries = await repo.GetAllAsync(ct);

        return summaries.Select(s => new BacktestRunView(
            s.RunId, s.StartedAtUtc, "completed",
            s.Symbol, s.Period, s.BacktestFrom, s.BacktestTo,
            s.InitialBalance, s.NetProfit, s.MaxDrawdownPct,
            s.TotalTrades, s.WinningTrades, s.WinRatePct,
            s.AlgoHash, s.ErrorMessage)).ToList();
    }

    public async Task<BacktestRunView?> GetRunAsync(string runId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
        var s = await repo.GetByIdAsync(runId, ct);
        if (s is null) return null;

        return new BacktestRunView(
            s.RunId, s.StartedAtUtc, "completed",
            s.Symbol, s.Period, s.BacktestFrom, s.BacktestTo,
            s.InitialBalance, s.NetProfit, s.MaxDrawdownPct,
            s.TotalTrades, s.WinningTrades, s.WinRatePct,
            s.AlgoHash, s.ErrorMessage);
    }
}

using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;

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

    public async Task<IReadOnlyList<StrategyPerformance>> GetStrategyBreakdownAsync(
        string runId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var evals = await db.BarEvaluations
            .Where(e => e.RunId == runId)
            .GroupBy(e => new { e.StrategyId, e.Reason, e.SignalFired })
            .Select(g => new
            {
                g.Key.StrategyId,
                g.Key.Reason,
                g.Key.SignalFired,
                Count = g.Count()
            })
            .ToListAsync(ct);

        var trades = await db.Trades
            .Where(t => t.RunId == runId)
            .GroupBy(t => t.StrategyId)
            .Select(g => new
            {
                StrategyId = g.Key,
                Total = g.Count(),
                Wins = g.Count(t => t.NetPnLAmount > 0)
            })
            .ToListAsync(ct);

        var tradeIndex = trades.ToDictionary(t => t.StrategyId);
        var strategyIds = evals.Select(e => e.StrategyId).Distinct().ToList();

        return strategyIds.Select(sid =>
        {
            var stratEvals = evals.Where(e => e.StrategyId == sid).ToList();
            var total   = stratEvals.Sum(e => e.Count);
            var signals = stratEvals.Where(e => e.SignalFired).Sum(e => e.Count);
            var noSignal = stratEvals
                .Where(e => !e.SignalFired)
                .OrderByDescending(e => e.Count)
                .Take(5)
                .Select(e => (e.Reason, e.Count))
                .ToList();

            var t = tradeIndex.GetValueOrDefault(sid);
            var wins   = t?.Wins ?? 0;
            var opened = t?.Total ?? 0;
            var losses = opened - wins;
            var wr = opened > 0 ? (double)wins / opened : 0d;

            return new StrategyPerformance(sid, total, signals, opened, wins, losses, wr,
                noSignal.AsReadOnly());
        }).ToList();
    }

    public async Task<IReadOnlyList<EquityPoint>> GetEquityAsync(
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        var query = db.EquitySnapshots.AsQueryable();
        if (from.HasValue) query = query.Where(e => e.TimestampUtc >= from.Value);
        if (to.HasValue) query = query.Where(e => e.TimestampUtc <= to.Value);

        try
        {
            return await query
                .OrderBy(e => e.TimestampUtc)
                .Select(e => new EquityPoint(
                    e.TimestampUtc,
                    e.Equity,
                    e.Balance))
                .ToListAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }
}

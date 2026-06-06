using Microsoft.EntityFrameworkCore;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteBacktestRunRepository(TradingDbContext db) : IBacktestRunRepository
{
    public async Task SaveAsync(BacktestRunSummary run, CancellationToken ct)
    {
        var entity = new BacktestRunEntity
        {
            RunId = run.RunId,
            StartedAtUtc = run.StartedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
            Symbol = run.Symbol,
            NetProfit = run.NetProfit,
            MaxDrawdownPct = run.MaxDrawdownPct,
            TotalTrades = run.TotalTrades,
            WinningTrades = run.WinningTrades,
            WinRatePct = run.WinRatePct,
            ExitCode = run.ExitCode,
            ErrorMessage = run.ErrorMessage,
        };
        db.BacktestRuns.Add(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<BacktestRunSummary>> GetAllAsync(CancellationToken ct)
    {
        return await db.BacktestRuns
            .OrderByDescending(r => r.StartedAtUtc)
            .Select(e => new BacktestRunSummary(
                e.RunId, e.StartedAtUtc, e.CompletedAtUtc,
                e.Symbol, e.NetProfit, e.MaxDrawdownPct,
                e.TotalTrades, e.WinningTrades, e.WinRatePct,
                e.ExitCode, e.ErrorMessage))
            .ToListAsync(ct);
    }

    public async Task<BacktestRunSummary?> GetByIdAsync(string runId, CancellationToken ct)
    {
        var e = await db.BacktestRuns.FindAsync([runId], ct);
        if (e is null) return null;
        return new BacktestRunSummary(
            e.RunId, e.StartedAtUtc, e.CompletedAtUtc,
            e.Symbol, e.NetProfit, e.MaxDrawdownPct,
            e.TotalTrades, e.WinningTrades, e.WinRatePct,
            e.ExitCode, e.ErrorMessage);
    }
}

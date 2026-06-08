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
            Period = run.Period,
            BacktestFrom = run.BacktestFrom,
            BacktestTo = run.BacktestTo,
            InitialBalance = run.InitialBalance,
            AlgoHash = run.AlgoHash,
            StrategyParamsJson = run.StrategyParamsJson,
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

    public async Task UpdateAsync(BacktestRunSummary run, CancellationToken ct)
    {
        var entity = await db.BacktestRuns.FindAsync([run.RunId], ct);
        if (entity is null) return;
        entity.CompletedAtUtc = run.CompletedAtUtc;
        entity.NetProfit = run.NetProfit;
        entity.MaxDrawdownPct = run.MaxDrawdownPct;
        entity.TotalTrades = run.TotalTrades;
        entity.WinningTrades = run.WinningTrades;
        entity.WinRatePct = run.WinRatePct;
        entity.ExitCode = run.ExitCode;
        entity.ErrorMessage = run.ErrorMessage;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<BacktestRunSummary>> GetAllAsync(CancellationToken ct)
    {
        return await db.BacktestRuns
            .OrderByDescending(r => r.StartedAtUtc)
            .Select(e => new BacktestRunSummary(
                e.RunId, e.StartedAtUtc, e.CompletedAtUtc,
                e.Symbol, e.Period, e.BacktestFrom, e.BacktestTo,
                e.InitialBalance, e.AlgoHash, e.StrategyParamsJson,
                e.NetProfit, e.MaxDrawdownPct,
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
            e.Symbol, e.Period, e.BacktestFrom, e.BacktestTo,
            e.InitialBalance, e.AlgoHash, e.StrategyParamsJson,
            e.NetProfit, e.MaxDrawdownPct,
            e.TotalTrades, e.WinningTrades, e.WinRatePct,
            e.ExitCode, e.ErrorMessage);
    }
}

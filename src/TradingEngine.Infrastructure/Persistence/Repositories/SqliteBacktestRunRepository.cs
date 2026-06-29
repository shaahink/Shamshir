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
            Symbols = run.Symbols,
            Periods = run.Periods,
            BacktestFrom = run.BacktestFrom,
            BacktestTo = run.BacktestTo,
            InitialBalance = run.InitialBalance,
            AlgoHash = run.AlgoHash,
            StrategyParamsJson = run.StrategyParamsJson,
            EffectiveConfigJson = run.EffectiveConfigJson,
            NetProfit = run.NetProfit,
            GrossPnL = run.GrossPnL,
            CommissionTotal = run.CommissionTotal,
            SwapTotal = run.SwapTotal,
            MaxDrawdownPct = run.MaxDrawdownPct,
            TotalTrades = run.TotalTrades,
            WinningTrades = run.WinningTrades,
            WinRatePct = run.WinRatePct,
            ExitCode = run.ExitCode,
            ErrorMessage = run.ErrorMessage,
            ReportJsonPath = run.ReportJsonPath,
            DatasetId = run.DatasetId,
            ConfigSetId = run.ConfigSetId,
            Seed = run.Seed,
            ParentRunId = run.ParentRunId,
            RunPlanJson = run.RunPlanJson,
            Venue = run.Venue,
            RiskProfileId = run.RiskProfileId,
            GovernorEnabled = run.GovernorEnabled,
            RegimeEnabled = run.RegimeEnabled,
            CommissionPerMillion = run.CommissionPerMillion,
            SpreadPips = run.SpreadPips,
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
        entity.GrossPnL = run.GrossPnL;
        entity.CommissionTotal = run.CommissionTotal;
        entity.SwapTotal = run.SwapTotal;
        entity.MaxDrawdownPct = run.MaxDrawdownPct;
        entity.TotalTrades = run.TotalTrades;
        entity.WinningTrades = run.WinningTrades;
        entity.WinRatePct = run.WinRatePct;
        entity.ExitCode = run.ExitCode;
        entity.ErrorMessage = run.ErrorMessage;
        entity.EffectiveConfigJson = run.EffectiveConfigJson;
        entity.ReportJsonPath = run.ReportJsonPath;
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<BacktestRunSummary>> GetAllAsync(CancellationToken ct)
    {
        var entities = await db.BacktestRuns
            .OrderByDescending(r => r.StartedAtUtc)
            .ToListAsync(ct);

        var result = new List<BacktestRunSummary>(entities.Count);
        foreach (var e in entities)
            result.Add(await ReconcileAsync(e, ct));
        return result;
    }

    public async Task<BacktestRunSummary?> GetByIdAsync(string runId, CancellationToken ct)
    {
        var e = await db.BacktestRuns.FindAsync([runId], ct);
        if (e is null) return null;
        return await ReconcileAsync(e, ct);
    }

    // A run interrupted after its trades were persisted but before WriteEndRecordAsync leaves the
    // summary at its start-record zeros (0 trades, ExitCode -1, CompletedAtUtc unset). Readers then
    // show "0 trades / 0 profit / running" for a run that clearly has trades.
    // iter-redesign P4.1: also catch runs where trades were written AND counted but the end-record
    // update failed (TotalTrades > 0, ExitCode -1 / CompletedAtUtc default). Re-derive from trades
    // table and persist the fix so the self-heal is durable.
    private async Task<BacktestRunSummary> ReconcileAsync(BacktestRunEntity e, CancellationToken ct)
    {
        var net = e.NetProfit;
        var grossPnL = e.GrossPnL;
        var commissionTotal = e.CommissionTotal;
        var swapTotal = e.SwapTotal;
        var maxDd = e.MaxDrawdownPct;
        var total = e.TotalTrades;
        var wins = e.WinningTrades;
        var winRate = e.WinRatePct;
        var completedAt = e.CompletedAtUtc;
        var exitCode = e.ExitCode;

        if (total == 0 || exitCode == -1 || completedAt == default)
        {
            var trades = await db.Trades
                .Where(t => t.RunId == e.RunId)
                .OrderBy(t => t.ClosedAtUtc)
                .Select(t => new { t.NetPnLAmount, t.GrossPnLAmount, t.CommissionAmount, t.SwapAmount, t.ClosedAtUtc })
                .ToListAsync(ct);

            if (trades.Count > 0)
            {
                total = trades.Count;
                wins = trades.Count(t => t.NetPnLAmount > 0);
                winRate = (double)wins / total;
                net = trades.Sum(t => t.NetPnLAmount);
                grossPnL = trades.Sum(t => t.GrossPnLAmount);
                commissionTotal = trades.Sum(t => t.CommissionAmount);
                swapTotal = trades.Sum(t => t.SwapAmount);

                var equity = e.InitialBalance;
                var peak = equity;
                maxDd = 0m;
                foreach (var t in trades)
                {
                    equity += t.NetPnLAmount;
                    if (equity > peak) peak = equity;
                    if (peak > 0)
                    {
                        var dd = (peak - equity) / peak;
                        if (dd > maxDd) maxDd = dd;
                    }
                }

                if (completedAt == default) completedAt = trades[^1].ClosedAtUtc;
                exitCode = 0;

                // P4.1: persist the re-derived values so the self-heal is durable.
                e.TotalTrades = total;
                e.WinningTrades = wins;
                e.WinRatePct = winRate;
                e.NetProfit = net;
                e.GrossPnL = grossPnL;
                e.CommissionTotal = commissionTotal;
                e.SwapTotal = swapTotal;
                e.MaxDrawdownPct = maxDd;
                e.CompletedAtUtc = completedAt;
                e.ExitCode = exitCode;
                await db.SaveChangesAsync(ct);
            }
        }

        return new BacktestRunSummary(
            e.RunId, e.StartedAtUtc, completedAt,
            e.Symbol, e.Period, e.Symbols, e.Periods, e.BacktestFrom, e.BacktestTo,
            e.InitialBalance, e.AlgoHash, e.StrategyParamsJson, e.EffectiveConfigJson,
            net, grossPnL, commissionTotal, swapTotal, maxDd, total, wins, winRate,
            exitCode, e.ErrorMessage, e.ReportJsonPath,
            e.DatasetId, e.ConfigSetId, e.Seed, e.ParentRunId,
            e.RunPlanJson, e.Venue, e.RiskProfileId, e.GovernorEnabled, e.RegimeEnabled,
            e.CommissionPerMillion, e.SpreadPips);
    }
}

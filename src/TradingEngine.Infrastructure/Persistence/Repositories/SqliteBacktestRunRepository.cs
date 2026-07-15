using Microsoft.EntityFrameworkCore;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteBacktestRunRepository(TradingDbContext db) : IBacktestRunRepository
{
    public async Task SaveAsync(BacktestRunSummary run, CancellationToken ct)
    {
        await RetryOnBusyAsync(async () =>
        {
            var existing = await db.BacktestRuns.FindAsync([run.RunId], ct);
            if (existing is not null)
            {
                MapToEntity(run, existing);
            }
            else
            {
                var entity = new BacktestRunEntity();
                MapToEntity(run, entity);
                db.BacktestRuns.Add(entity);
            }
            await db.SaveChangesAsync(ct);
        }, ct);
    }

    private static void MapToEntity(BacktestRunSummary run, BacktestRunEntity entity)
    {
        entity.RunId = run.RunId;
        entity.StartedAtUtc = run.StartedAtUtc;
        entity.CompletedAtUtc = run.CompletedAtUtc;
        entity.Symbol = run.Symbol;
        entity.Period = run.Period;
        entity.Symbols = run.Symbols;
        entity.Periods = run.Periods;
        entity.BacktestFrom = run.BacktestFrom;
        entity.BacktestTo = run.BacktestTo;
        entity.InitialBalance = run.InitialBalance;
        entity.AlgoHash = run.AlgoHash;
        entity.StrategyParamsJson = run.StrategyParamsJson;
        entity.EffectiveConfigJson = run.EffectiveConfigJson;
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
        entity.WarningsJson = run.WarningsJson;
        entity.ReportJsonPath = run.ReportJsonPath;
        entity.DatasetId = run.DatasetId;
        entity.ConfigSetId = run.ConfigSetId;
        entity.Seed = run.Seed;
        entity.ParentRunId = run.ParentRunId;
        entity.ComparePairId = run.ComparePairId;
        entity.RunPlanJson = run.RunPlanJson;
        entity.Venue = run.Venue;
        entity.RiskProfileId = run.RiskProfileId;
        entity.GovernorEnabled = run.GovernorEnabled;
        entity.RegimeEnabled = run.RegimeEnabled;
        entity.CommissionPerMillion = run.CommissionPerMillion;
        entity.SpreadPips = run.SpreadPips;
        entity.WallElapsedMs = run.WallElapsedMs;
        entity.BarsPerSec = run.BarsPerSec;
        entity.TotalBars = run.TotalBars;
        entity.ExplorationMode = run.ExplorationMode;
        entity.RecordExcursions = run.RecordExcursions;
        entity.Status = run.Status ?? "";
        entity.QueuePosition = run.QueuePosition;
    }

    public async Task UpdateAsync(BacktestRunSummary run, CancellationToken ct)
    {
        await RetryOnBusyAsync(async () =>
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
        entity.WarningsJson = run.WarningsJson;
        entity.EffectiveConfigJson = run.EffectiveConfigJson;
        entity.ReportJsonPath = run.ReportJsonPath;
        entity.WallElapsedMs = run.WallElapsedMs;
        entity.BarsPerSec = run.BarsPerSec;
        entity.TotalBars = run.TotalBars;
        entity.Status = run.Status ?? "";
        entity.QueuePosition = run.QueuePosition;
        await db.SaveChangesAsync(ct);
        }, ct);
    }

    private static async Task RetryOnBusyAsync(Func<Task> action, CancellationToken ct, int maxRetries = 3)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 5 && attempt < maxRetries)
            {
                await Task.Delay(100 << attempt, ct);
            }
        }
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
        // P1.2: also reconcile when TotalTrades is nonzero but wrong — cTrader path
        // writes stats BEFORE all venue trades land (D2). Recompute from ledger.
        else
        {
            var actualCount = await db.Trades
                .Where(t => t.RunId == e.RunId)
                .CountAsync(ct);

            if (actualCount > 0 && actualCount != total)
            {
                total = actualCount;
                var trades = await db.Trades
                    .Where(t => t.RunId == e.RunId)
                    .OrderBy(t => t.ClosedAtUtc)
                    .Select(t => new { t.NetPnLAmount, t.GrossPnLAmount, t.CommissionAmount, t.SwapAmount, t.ClosedAtUtc })
                    .ToListAsync(ct);

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

                e.TotalTrades = total;
                e.WinningTrades = wins;
                e.WinRatePct = winRate;
                e.NetProfit = net;
                e.GrossPnL = grossPnL;
                e.CommissionTotal = commissionTotal;
                e.SwapTotal = swapTotal;
                e.MaxDrawdownPct = maxDd;
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
            e.CommissionPerMillion, e.SpreadPips,
            e.WallElapsedMs, e.BarsPerSec, e.TotalBars, e.WarningsJson, e.ComparePairId,
            e.ExplorationMode, e.RecordExcursions,
            e.Status, e.QueuePosition);
    }

    public async Task DeleteAsync(string runId, CancellationToken ct)
    {
        await db.Trades.Where(t => t.RunId == runId).ExecuteDeleteAsync(ct);
        await db.JournalEntries.Where(j => j.RunId == runId).ExecuteDeleteAsync(ct);
        await db.EquitySnapshots.Where(e => e.RunId == runId).ExecuteDeleteAsync(ct);
        await db.Bars.Where(b => b.RunId == runId).ExecuteDeleteAsync(ct);
        await db.VenueSessions.Where(v => v.RunId == runId).ExecuteDeleteAsync(ct);
        await db.BacktestRuns.Where(r => r.RunId == runId).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> DeleteRunsAsync(IReadOnlyCollection<string> runIds, CancellationToken ct)
    {
        if (runIds.Count == 0) return 0;
        var ids = runIds.ToHashSet();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.Trades.Where(t => t.RunId != null && ids.Contains(t.RunId)).ExecuteDeleteAsync(ct);
        await db.JournalEntries.Where(j => ids.Contains(j.RunId)).ExecuteDeleteAsync(ct);
        await db.EquitySnapshots.Where(s => s.RunId != null && ids.Contains(s.RunId)).ExecuteDeleteAsync(ct);
        await db.Bars.Where(b => ids.Contains(b.RunId)).ExecuteDeleteAsync(ct);
        await db.VenueSessions.Where(v => ids.Contains(v.RunId)).ExecuteDeleteAsync(ct);
        var deleted = await db.BacktestRuns.Where(r => ids.Contains(r.RunId)).ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return deleted;
    }
}

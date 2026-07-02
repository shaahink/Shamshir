using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Reconcile;

namespace TradingEngine.Web.Services;

public sealed class LedgerReconcileService
{
    private readonly TradingDbContext _db;

    public LedgerReconcileService(TradingDbContext db)
    {
        _db = db;
    }

    public async Task<ReconcileLedger> BuildEngineLedgerAsync(string runId, CancellationToken ct)
    {
        var run = await _db.BacktestRuns.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RunId == runId, ct);

        if (run is null)
            throw new InvalidOperationException($"Run {runId} not found.");

        var trades = await _db.Trades.AsNoTracking()
            .Where(t => t.RunId == runId)
            .OrderBy(t => t.ClosedAtUtc)
            .Select(t => new ReconcileTrade(
                t.OpenedAtUtc,
                t.ClosedAtUtc,
                t.Direction,
                t.Lots,
                t.EntryPrice,
                t.ExitPrice,
                t.NetPnLAmount,
                t.ExitReason))
            .ToListAsync(ct);

        return new ReconcileLedger(
            Source: $"engine:{runId}",
            NetProfit: run.NetProfit,
            GrossProfit: run.GrossPnL,
            Commission: run.CommissionTotal,
            Swap: run.SwapTotal,
            MaxDrawdownPct: (double)run.MaxDrawdownPct,
            TotalTrades: run.TotalTrades,
            WinningTrades: run.WinningTrades,
            WinRatePct: run.WinRatePct,
            Trades: trades);
    }
}

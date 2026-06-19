using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Tests.Simulation.Verification;

public static class CtraderDiffHarness
{
    public record ToleranceConfig(
        decimal NetPnLPct = 0.01m,
        decimal NetPnLAbs = 5m,
        decimal MaxDdPctAbsolute = 0.02m,
        decimal CommissionAbs = 0.01m,
        decimal SwapAbs = 0.01m);

    public static async Task<CtraderDiffResult> CompareAsync(
        TradingDbContext db, string runId, string reportJsonPath,
        ToleranceConfig? toleranceConfig = null, CancellationToken ct = default)
    {
        var tolerance = toleranceConfig ?? new ToleranceConfig();
        var result = new CtraderDiffResult
        {
            RunId = runId,
            ReportJsonPath = reportJsonPath,
        };

        var parsed = CtraderJsonReport.Parse(reportJsonPath);
        if (parsed.IsEmpty)
        {
            result.Discrepancies.Add(new CtraderDiscrepancy(
                "ReportJson", DiscrepancyKind.Structural, Severity.Error,
                "cTrader report not found, empty, or failed to parse",
                reportJsonPath, "empty"));
            return result;
        }

        var dbTradesQuery = db.Trades
            .Where(t => t.RunId == runId)
            .OrderBy(t => t.ClosedAtUtc);

        var dbTrades = await EntityFrameworkQueryableExtensions.ToListAsync(dbTradesQuery, ct);

        var dbRun = await db.BacktestRuns.FindAsync([runId], ct);

        // DB max drawdown from the engine's per-bar equity snapshots (captures floating/intra-trade DD,
        // unlike the trade-close balance series and unlike the often-absent run summary).
        var dbMaxDdPct = await EntityFrameworkQueryableExtensions.MaxAsync(
            db.EquitySnapshots.Where(s => s.RunId == runId).Select(s => (decimal?)s.CurrentMaxDrawdown), ct) ?? 0m;
        if (dbMaxDdPct == 0m && dbRun is not null) dbMaxDdPct = dbRun.MaxDrawdownPct;

        if (parsed.HasSummary)
        {
            CompareSummaryReport(result, parsed.Summary!, dbTrades, dbRun, dbMaxDdPct, tolerance);
            CompareTradeHistory(result, parsed.Summary!.History, dbTrades, tolerance);
        }
        else
        {
            CompareSummaryEvents(result, parsed.Events, dbTrades, dbRun, dbMaxDdPct, tolerance);
        }
        CompareTradeIntegrity(result, parsed.Events, dbTrades, tolerance);

        return result;
    }

    private static void CompareSummaryReport(
        CtraderDiffResult result, CtraderSummaryReport report,
        List<TradeResultEntity> dbTrades,
        BacktestRunEntity? dbRun, decimal dbMaxDdPct, ToleranceConfig tolerance)
    {
        var main = report.Main;
        var stats = report.TradeStatistics;
        var equity = report.Equity;

        var ctraderTradeCount = (int)(stats?.TotalTrades?.All ?? 0);
        var ctraderNetProfit = stats?.NetProfit?.All ?? main?.NetProfit ?? 0;
        var ctraderMaxDdPct = (equity?.MaxEquityDrawdownPercent ?? 0) / 100m;
        var ctraderWinning = (int)(stats?.WinningTrades?.All ?? 0);

        result.CtraderTradeCount = ctraderTradeCount;
        result.DbTradeCount = dbTrades.Count;
        result.CtraderNetProfit = ctraderNetProfit;
        result.DbNetProfit = dbRun?.NetProfit ?? dbTrades.Sum(t => t.NetPnLAmount);
        result.CtraderMaxDdPct = ctraderMaxDdPct;
        result.DbMaxDdPct = dbMaxDdPct;
        result.CtraderWinningTrades = ctraderWinning;
        result.DbWinningTrades = dbRun?.WinningTrades ?? dbTrades.Count(t => t.NetPnLAmount > 0);
        result.CtraderCommission = stats?.Commissions?.All;
        result.DbCommission = dbTrades.Sum(t => t.CommissionAmount);
        result.CtraderSwap = stats?.Swaps?.All;
        result.DbSwap = dbTrades.Sum(t => t.SwapAmount);

        var dbNet = result.DbNetProfit;

        if (ctraderTradeCount != dbTrades.Count)
        {
            result.Discrepancies.Add(new CtraderDiscrepancy(
                "TradeCount", DiscrepancyKind.Structural, Severity.Error,
                $"Trade count mismatch — cTrader: {ctraderTradeCount}, DB: {dbTrades.Count}",
                ctraderTradeCount.ToString(), dbTrades.Count.ToString()));
        }

        if (ctraderNetProfit != 0 && dbNet != 0)
        {
            var pnlDelta = Math.Abs(ctraderNetProfit - dbNet);
            var pnlPct = dbNet != 0 ? pnlDelta / Math.Abs(dbNet) : pnlDelta;
            if (pnlPct > tolerance.NetPnLPct && pnlDelta > tolerance.NetPnLAbs)
            {
                result.Discrepancies.Add(new CtraderDiscrepancy(
                    "NetProfit", DiscrepancyKind.Numeric, Severity.Warning,
                    $"Net PnL delta: {pnlDelta:F2} ({pnlPct:P1})",
                    ctraderNetProfit.ToString("F2"), dbNet.ToString("F2")));
            }
        }

        if (ctraderMaxDdPct != 0 && result.DbMaxDdPct != 0)
        {
            var ddDelta = Math.Abs(ctraderMaxDdPct - result.DbMaxDdPct);
            if (ddDelta > tolerance.MaxDdPctAbsolute)
            {
                result.Discrepancies.Add(new CtraderDiscrepancy(
                    "MaxDrawdown", DiscrepancyKind.Numeric, Severity.Warning,
                    $"Max DD% delta: {ddDelta:P1}",
                    ctraderMaxDdPct.ToString("P2"), result.DbMaxDdPct.ToString("P2")));
            }
        }

        var ctraderCommission = result.CtraderCommission ?? 0;
        if (ctraderCommission != 0 && Math.Abs(ctraderCommission - result.DbCommission) > tolerance.CommissionAbs)
        {
            result.Discrepancies.Add(new CtraderDiscrepancy(
                "Commission", DiscrepancyKind.Numeric, Severity.Info,
                "Commission delta",
                ctraderCommission.ToString("F2"), result.DbCommission.ToString("F2")));
        }

        var ctraderSwap = result.CtraderSwap ?? 0;
        if (ctraderSwap != 0 && Math.Abs(ctraderSwap - result.DbSwap) > tolerance.SwapAbs)
        {
            result.Discrepancies.Add(new CtraderDiscrepancy(
                "Swap", DiscrepancyKind.Numeric, Severity.Info,
                "Swap delta",
                ctraderSwap.ToString("F2"), result.DbSwap.ToString("F2")));
        }
    }

    private static void CompareSummaryEvents(
        CtraderDiffResult result, List<CtraderJsonReport> events,
        List<TradeResultEntity> dbTrades,
        BacktestRunEntity? dbRun, decimal dbMaxDdPct, ToleranceConfig tolerance)
    {
        var closes = events.Where(e => e.IsClosed).ToList();
        var ctraderTradeCount = closes.Count;
        var ctraderNetProfit = closes.Sum(e => e.GrossProfit ?? 0m);
        var ctraderWinning = closes.Count(e => (e.GrossProfit ?? 0) > 0);

        result.CtraderTradeCount = ctraderTradeCount;
        result.DbTradeCount = dbTrades.Count;
        result.CtraderNetProfit = ctraderNetProfit;
        result.DbNetProfit = dbRun?.NetProfit ?? dbTrades.Sum(t => t.NetPnLAmount);
        result.CtraderMaxDdPct = 0;
        result.DbMaxDdPct = dbMaxDdPct;
        result.CtraderWinningTrades = ctraderWinning;
        result.DbWinningTrades = dbRun?.WinningTrades ?? dbTrades.Count(t => t.NetPnLAmount > 0);
        result.CtraderCommission = null;
        result.DbCommission = dbTrades.Sum(t => t.CommissionAmount);
        result.CtraderSwap = null;
        result.DbSwap = dbTrades.Sum(t => t.SwapAmount);

        var dbNet = result.DbNetProfit;

        if (ctraderTradeCount != dbTrades.Count)
        {
            result.Discrepancies.Add(new CtraderDiscrepancy(
                "TradeCount", DiscrepancyKind.Structural, Severity.Error,
                $"Trade count mismatch — cTrader: {ctraderTradeCount}, DB: {dbTrades.Count}",
                ctraderTradeCount.ToString(), dbTrades.Count.ToString()));
        }

        if (ctraderNetProfit != 0 && dbNet != 0)
        {
            var pnlDelta = Math.Abs(ctraderNetProfit - dbNet);
            var pnlPct = dbNet != 0 ? pnlDelta / Math.Abs(dbNet) : pnlDelta;
            if (pnlPct > tolerance.NetPnLPct && pnlDelta > tolerance.NetPnLAbs)
            {
                result.Discrepancies.Add(new CtraderDiscrepancy(
                    "NetProfit", DiscrepancyKind.Numeric, Severity.Warning,
                    $"Net PnL delta: {pnlDelta:F2} ({pnlPct:P1})",
                    ctraderNetProfit.ToString("F2"), dbNet.ToString("F2")));
            }
        }

        if (ctraderWinning != result.DbWinningTrades)
        {
            result.Discrepancies.Add(new CtraderDiscrepancy(
                "WinningTrades", DiscrepancyKind.Numeric, Severity.Info,
                "Winning trade count mismatch",
                ctraderWinning.ToString(), result.DbWinningTrades.ToString()));
        }
    }

    // Per-trade reconciliation: join the venue history[] (cBot ledger) to DB trades and compare each
    // trade's economics — surfacing per-trade drift (fill price, PnL, commission, swap) and trades
    // present on only one side.
    //
    // Join key: the engine assigns a PositionId distinct from the venue clientOrderId, and the
    // originating OrderId is not yet persisted on the trade, so we match by economic identity
    // (direction + entry price, nearest open time). Once OrderId is persisted on TradeResult this
    // should switch to an exact clientOrderId==OrderId join. See [[project-ctrader-report-extraction]].
    private static void CompareTradeHistory(
        CtraderDiffResult result, CtraderHistorySection? history,
        List<TradeResultEntity> dbTrades, ToleranceConfig tolerance)
    {
        if (history is null || history.Items.Count == 0) return;

        var remaining = new List<TradeResultEntity>(dbTrades);
        foreach (var v in history.Items)
        {
            var venueOpen = DateTimeOffset.FromUnixTimeMilliseconds(v.EntryTime).UtcDateTime;
            var match = remaining
                .Where(t => SameDirection(t.Direction, v.Direction)
                            && Math.Abs(t.EntryPrice - v.EntryPrice) <= 0.00005m)
                .OrderBy(t => Math.Abs((venueOpen - t.OpenedAtUtc).TotalSeconds))
                .FirstOrDefault();

            var label = Short(v.ClientOrderId ?? v.Id.ToString());
            if (match is null)
            {
                result.Discrepancies.Add(new CtraderDiscrepancy(
                    "TradeMissingInDb", DiscrepancyKind.Structural, Severity.Error,
                    $"Venue trade {label} ({v.Direction} @ {v.EntryPrice:0.#####}, net {v.Net:F2}) has no matching DB row",
                    "present", "missing"));
                continue;
            }
            remaining.Remove(match);
            CompareField(result, label, "Net", v.Net, match.NetPnLAmount, tolerance.NetPnLAbs);
            CompareField(result, label, "Gross", v.Gross, match.GrossPnLAmount, tolerance.NetPnLAbs);
            CompareField(result, label, "Commission", v.Commissions, match.CommissionAmount, tolerance.CommissionAbs);
            CompareField(result, label, "Swap", v.Swaps, match.SwapAmount, tolerance.SwapAbs);
            CompareField(result, label, "ExitPrice", v.ClosePrice, match.ExitPrice, 0.00005m);
        }

        foreach (var t in remaining)
        {
            result.Discrepancies.Add(new CtraderDiscrepancy(
                "TradeMissingInVenue", DiscrepancyKind.Structural, Severity.Error,
                $"DB trade {Short(t.PositionId.ToString())} ({t.Direction} @ {t.EntryPrice:0.#####}, net {t.NetPnLAmount:F2}) has no matching venue row",
                "missing", "present"));
        }
    }

    private static bool SameDirection(string db, string? venue)
    {
        if (venue is null) return false;
        var d = db.Equals("Long", StringComparison.OrdinalIgnoreCase) || db.Equals("Buy", StringComparison.OrdinalIgnoreCase);
        var v = venue.Equals("Long", StringComparison.OrdinalIgnoreCase) || venue.Equals("Buy", StringComparison.OrdinalIgnoreCase);
        return d == v;
    }

    private static string Short(string id) => id.Length > 8 ? id[..8] : id;

    private static void CompareField(CtraderDiffResult result, string label, string field,
        decimal venue, decimal db, decimal tol)
    {
        if (Math.Abs(venue - db) <= tol) return;
        result.Discrepancies.Add(new CtraderDiscrepancy(
            $"Trade{field}", DiscrepancyKind.Numeric, Severity.Warning,
            $"Trade {label} {field}: venue {venue:0.#####} vs DB {db:0.#####}",
            venue.ToString("0.#####"), db.ToString("0.#####")));
    }

    private static void CompareTradeIntegrity(
        CtraderDiffResult result, List<CtraderJsonReport> events,
        List<TradeResultEntity> dbTrades,
        ToleranceConfig tolerance)
    {
        foreach (var t in dbTrades)
        {
            if (t.EntryPrice <= 0)
            {
                result.Discrepancies.Add(new CtraderDiscrepancy(
                    "TradeEntryPrice", DiscrepancyKind.Structural, Severity.Error,
                    $"Trade {t.Id} has zero/negative entry price {t.EntryPrice}",
                    ">0", t.EntryPrice.ToString("F5")));
            }

            if (t.ExitPrice <= 0 && t.ExitReason is "SL" or "TP")
            {
                result.Discrepancies.Add(new CtraderDiscrepancy(
                    "TradeExitPrice", DiscrepancyKind.Structural, Severity.Error,
                    $"SL/TP close for trade {t.Id} has zero exit price. Reason={t.ExitReason}",
                    ">0", "0"));
            }

            if (string.IsNullOrWhiteSpace(t.ExitReason))
            {
                result.Discrepancies.Add(new CtraderDiscrepancy(
                    "TradeExitReason", DiscrepancyKind.Structural, Severity.Warning,
                    $"Trade {t.Id} has empty ExitReason",
                    "non-empty", "empty"));
            }

            if (t.NetPnLAbsentButHadMovement())
            {
                result.Discrepancies.Add(new CtraderDiscrepancy(
                    "TradeZeroPnL", DiscrepancyKind.Structural, Severity.Error,
                    $"Trade {t.Id} has non-trivial price move but $0 PnL. Entry={t.EntryPrice:F5} Exit={t.ExitPrice:F5} Lots={t.Lots}",
                    "!=0", "0"));
            }
        }
    }
}

internal static class TradeResultExtensions
{
    public static bool NetPnLAbsentButHadMovement(this TradeResultEntity t) =>
        t.NetPnLAmount == 0m
        && t.ExitPrice > 0
        && t.Lots >= 0.05m
        && Math.Abs(t.ExitPrice - t.EntryPrice) > 0.0010m;
}

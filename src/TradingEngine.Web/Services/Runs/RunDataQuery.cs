using TradingEngine.Web.Dtos.Runs;

namespace TradingEngine.Web.Services;

/// <summary>The run-data read paths (trades, equity, daily PnL, analytics): served from the
/// in-memory RunDataCache while the run is live, from the DB once it isn't. Extracted verbatim
/// from RunQueryService.</summary>
public sealed class RunDataQuery(
    TradingDbContext db,
    IEquityRepository equityRepo,
    IRunDataCache? runDataCache)
{
    private readonly TradingDbContext _db = db;
    private readonly IEquityRepository _equityRepo = equityRepo;
    private readonly IRunDataCache? _cache = runDataCache;

    public async Task<IReadOnlyList<TradeSummaryResponse>> GetRunTradesAsync(string runId, CancellationToken ct)
    {
        if (_cache is not null && _cache.HasRun(runId))
        {
            var cachedTrades = _cache.GetTrades(runId);
            if (cachedTrades.Count > 0)
            {
                return cachedTrades.Select(t => new TradeSummaryResponse
                {
                    Id = t.Id, PositionId = t.PositionId, OrderId = t.OrderId,
                    Symbol = t.Symbol.Value, Direction = t.Direction.ToString(), Lots = t.Lots,
                    EntryPrice = t.EntryPrice.Value, ExitPrice = t.ExitPrice.Value,
                    StopLoss = t.StopLoss.Value == 0 ? null : t.StopLoss.Value,
                    TakeProfit = t.TakeProfit?.Value,
                    OpenedAtUtc = t.OpenedAtUtc, ClosedAtUtc = t.ClosedAtUtc,
                    GrossPnLAmount = t.GrossPnL.Amount, CommissionAmount = t.Commission.Amount,
                    SwapAmount = t.Swap.Amount, NetPnLAmount = t.NetPnL.Amount,
                    PnLPips = t.PnLPips.Value, RMultiple = t.RMultiple,
                    MaxAdverseExcursion = t.MaxAdverseExcursion.Value, MaxFavorableExcursion = t.MaxFavorableExcursion.Value,
                    MaeR = t.MaeR, MfeR = t.MfeR,
                    ExitReason = t.ExitReason, StrategyId = t.StrategyId,
                    DurationSeconds = t.DurationSeconds, EntryType = t.OrderEntryMethod,
                    EntryReason = t.EntryReason, EntryRegime = t.EntryRegime,
                    EntrySnapshotJson = t.EntrySnapshotJson, ExitDetailJson = t.ExitDetailJson,
                }).ToList();
            }
        }

        return await _db.Trades
            .AsNoTracking()
            .Where(t => t.RunId == runId)
            .OrderByDescending(t => t.ClosedAtUtc)
            .Select(t => new TradeSummaryResponse
            {
                Id = t.Id,
                PositionId = t.PositionId,
                OrderId = t.OrderId,
                Symbol = t.Symbol,
                Direction = t.Direction,
                Lots = t.Lots,
                EntryPrice = t.EntryPrice,
                ExitPrice = t.ExitPrice,
                StopLoss = t.StopLoss,
                TakeProfit = t.TakeProfit,
                OpenedAtUtc = t.OpenedAtUtc,
                ClosedAtUtc = t.ClosedAtUtc,
                GrossPnLAmount = t.GrossPnLAmount,
                CommissionAmount = t.CommissionAmount,
                SwapAmount = t.SwapAmount,
                NetPnLAmount = t.NetPnLAmount,
                PnLPips = t.PnLPips,
                RMultiple = t.RMultiple,
                MaxAdverseExcursion = t.MaxAdverseExcursion,
                MaxFavorableExcursion = t.MaxFavorableExcursion,
                MaeR = t.MaeR,
                MfeR = t.MfeR,
                ExitReason = t.ExitReason,
                StrategyId = t.StrategyId,
                DurationSeconds = t.DurationSeconds,
                EntryType = t.OrderEntryMethod,
                EntryReason = t.EntryReason,
                EntryRegime = t.EntryRegime,
                EntrySnapshotJson = t.EntrySnapshotJson,
                ExitDetailJson = t.ExitDetailJson,
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EquityPointResponse>> GetRunEquityAsync(string runId, CancellationToken ct)
    {
        if (_cache is not null && _cache.HasRun(runId))
        {
            var cached = _cache.GetEquity(runId);
            if (cached.Count > 0)
            {
                return cached.Select(e => new EquityPointResponse
                {
                    TimestampUtc = e.TimestampUtc, Equity = e.Equity, Balance = e.Balance,
                }).ToList();
            }
        }

        var snapshots = await _equityRepo.GetByRunIdAsync(runId, ct);
        return snapshots.Select(s => new EquityPointResponse
        {
            TimestampUtc = s.TimestampUtc, Equity = s.Equity, Balance = s.Balance,
        }).ToList();
    }

    public async Task<IReadOnlyList<DailyPnlResponse>> GetRunDailyPnLAsync(string runId, CancellationToken ct)
    {
        if (_cache is not null && _cache.HasRun(runId))
        {
            var cachedTrades = _cache.GetTrades(runId);
            if (cachedTrades.Count > 0)
            {
                return cachedTrades
                    .GroupBy(t => PropFirmDayOf(t.ClosedAtUtc))
                    .Select(g => new DailyPnlResponse
                    {
                        Date = g.Key.ToString("yyyy-MM-dd"),
                        PnL = g.Sum(t => t.NetPnL.Amount),
                    })
                    .ToList();
            }
        }

        var trades = await _db.Trades
            .AsNoTracking()
            .Where(t => t.RunId == runId)
            .OrderBy(t => t.ClosedAtUtc)
            .ToListAsync(ct);

        return trades.GroupBy(t => PropFirmDayOf(t.ClosedAtUtc))
            .Select(g => new DailyPnlResponse
            {
                Date = g.Key.ToString("yyyy-MM-dd"),
                PnL = g.Sum(t => t.NetPnLAmount),
            })
            .ToList();
    }

    /// <summary>
    /// The prop-firm reset-period date a UTC instant belongs to: midnight Europe/Prague — FTMO's
    /// verified reset (V0, 2026-07-16), matching the corrected rulesets' <c>dailyResetTimeUtc</c>
    /// of 00:00 in <c>dailyResetTimezone</c> as <c>ResetClock</c> interprets them. Daily PnL/DD
    /// MUST bucket on this boundary, not calendar UTC midnight — see iter-merge-plan PLAN.md
    /// "What NOT to do". (Runs recorded before V0 used the old 22:00-Prague boundary; their
    /// display buckets can differ from engine-truth buckets by up to 2 h at the edges.)
    /// </summary>
    private static DateOnly PropFirmDayOf(DateTime closedAtUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(closedAtUtc, DateTimeKind.Utc), PragueTz);
        return DateOnly.FromDateTime(local);
    }

    private static readonly TimeZoneInfo PragueTz = ResolvePragueTz();

    private static TimeZoneInfo ResolvePragueTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague"); }
        catch { return TimeZoneInfo.Utc; }
    }

    public async Task<RunAnalyticsResponse?> GetRunAnalyticsAsync(string runId, CancellationToken ct)
    {
        if (_cache is not null && _cache.HasRun(runId))
        {
            var cachedTrades = _cache.GetTrades(runId);
            if (cachedTrades.Count > 0)
            {
                return new RunAnalyticsResponse
                {
                    RMultiples = cachedTrades.Select(t => (double)t.RMultiple).ToList(),
                    HoldingTimes = cachedTrades.Select(t => (double)t.DurationSeconds).ToList(),
                    PnlByHour = cachedTrades.GroupBy(t => t.ClosedAtUtc.Hour)
                        .Select(g => new AnalyticsBucket { Key = g.Key.ToString(), Value = g.Sum(t => (double)t.NetPnL.Amount) })
                        .ToList(),
                    PnlByDay = cachedTrades.GroupBy(t => t.ClosedAtUtc.DayOfWeek)
                        .Select(g => new AnalyticsBucket { Key = g.Key.ToString(), Value = g.Sum(t => (double)t.NetPnL.Amount) })
                        .ToList(),
                    MaeMfe = cachedTrades.Select(t => new MaeMfePoint { X = -t.MaxAdverseExcursion.Value, Y = t.MaxFavorableExcursion.Value }).ToList(),
                };
            }
        }

        var trades = await _db.Trades
            .AsNoTracking()
            .Where(t => t.RunId == runId)
            .OrderBy(t => t.ClosedAtUtc)
            .ToListAsync(ct);

        if (trades.Count == 0)
        {
            return null;
        }

        return new RunAnalyticsResponse
        {
            RMultiples = trades.Select(t => t.RMultiple).ToList(),
            HoldingTimes = trades.Select(t => Math.Min(t.DurationSeconds, 3600)).ToList(),
            PnlByHour = trades.GroupBy(t => t.ClosedAtUtc.Hour)
                .Select(g => new AnalyticsBucket { Key = g.Key.ToString(), Value = g.Sum(t => (double)t.NetPnLAmount) })
                .ToList(),
            PnlByDay = trades.GroupBy(t => t.ClosedAtUtc.DayOfWeek)
                .Select(g => new AnalyticsBucket { Key = g.Key.ToString(), Value = g.Sum(t => (double)t.NetPnLAmount) })
                .ToList(),
            MaeMfe = trades.Select(t => new MaeMfePoint { X = -t.MaxAdverseExcursion, Y = t.MaxFavorableExcursion }).ToList(),
        };
    }
}

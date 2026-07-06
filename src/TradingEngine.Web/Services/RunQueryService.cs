using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Web.Dtos.Runs;

namespace TradingEngine.Web.Services;

public sealed class RunQueryService : IRunQueryService
{
    private readonly TradingDbContext _db;
    private readonly IBacktestRunRepository _runRepo;
    private readonly IEquityRepository _equityRepo;
    private readonly IJournalQueryRepository _journals;
    private readonly IRunDataCache? _cache;
    private readonly IMemoryCache? _memoryCache;
    private readonly BacktestOrchestrator? _orchestrator;

    private const int MaxBarEvents = 5000;
    private static readonly TimeSpan RunsListCacheDuration = TimeSpan.FromSeconds(2);
    private const string RunsListCacheKey = "runs:all";

    public RunQueryService(
        TradingDbContext db,
        IBacktestRunRepository runRepo,
        IEquityRepository equityRepo,
        IJournalQueryRepository journals,
        IMemoryCache? memoryCache = null,
        IRunDataCache? runDataCache = null,
        BacktestOrchestrator? orchestrator = null)
    {
        _db = db;
        _runRepo = runRepo;
        _equityRepo = equityRepo;
        _journals = journals;
        _memoryCache = memoryCache;
        _cache = runDataCache;
        _orchestrator = orchestrator;
    }

    public async Task<IReadOnlyList<RunListResponse>> GetRunsAsync(CancellationToken ct)
    {
        if (_memoryCache is not null && _memoryCache.TryGetValue(RunsListCacheKey, out IReadOnlyList<RunListResponse>? cached) && cached is not null)
            return cached;

        var runs = await _db.BacktestRuns
            .AsNoTracking()
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(50)
            .Select(r => new RunListResponse
            {
                RunId = r.RunId,
                CreatedAtUtc = r.CreatedAtUtc,
                Status = r.CompletedAtUtc == default ? "running"
                    : r.ErrorMessage != null ? "failed"
                    : "completed",
                Symbol = r.Symbol,
                Period = r.Period,
                Symbols = r.Symbols,
                Periods = r.Periods,
                StartedAtUtc = r.StartedAtUtc,
                CompletedAtUtc = r.CompletedAtUtc == default ? null : r.CompletedAtUtc,
                NetProfit = r.NetProfit,
                GrossPnL = r.GrossPnL,
                CommissionTotal = r.CommissionTotal,
                SwapTotal = r.SwapTotal,
                MaxDrawdownPct = r.MaxDrawdownPct,
                TotalTrades = r.TotalTrades,
                WinningTrades = r.WinningTrades,
                WinRatePct = r.WinRatePct,
                ErrorMessage = r.ErrorMessage,
            Venue = r.Venue ?? "replay",
                RiskProfileId = r.RiskProfileId,
            })
            .ToListAsync(ct);

        FixStuckRunStatuses(runs);

        _memoryCache?.Set(RunsListCacheKey, runs, RunsListCacheDuration);
        return runs;
    }

    public void InvalidateRunsCache() => _memoryCache?.Remove(RunsListCacheKey);

    public async Task<RunDetailResponse?> GetRunAsync(string runId, CancellationToken ct)
    {
        if (_orchestrator is not null)
        {
            var state = _orchestrator.GetState(runId);
            if (state is not null && state.Status is not "completed" and not "failed" and not "cancelled")
            {
                return BuildRunDetailFromState(state);
            }
        }

        var r = await _runRepo.GetByIdAsync(runId, ct);
        if (r is null) return null;

        return new RunDetailResponse
        {
            RunId = r.RunId,
            Status = ResolveStatus(r, _orchestrator),
            Symbol = r.Symbol,
            Period = r.Period,
            Symbols = r.Symbols,
            Periods = r.Periods,
            StartedAtUtc = r.StartedAtUtc,
            CompletedAtUtc = r.CompletedAtUtc == default ? null : r.CompletedAtUtc,
            BacktestFrom = r.BacktestFrom,
            BacktestTo = r.BacktestTo,
            InitialBalance = r.InitialBalance,
            NetProfit = r.NetProfit,
            GrossPnL = r.GrossPnL,
            CommissionTotal = r.CommissionTotal,
            SwapTotal = r.SwapTotal,
            MaxDrawdownPct = r.MaxDrawdownPct,
            TotalTrades = r.TotalTrades,
            WinningTrades = r.WinningTrades,
            WinRatePct = r.WinRatePct,
            ErrorMessage = r.ErrorMessage,
            ExitCode = r.ExitCode,
            EffectiveConfigJson = r.EffectiveConfigJson,
            ReportJsonPath = r.ReportJsonPath,
            RunPlanJson = r.RunPlanJson,
            Venue = r.Venue,
            RiskProfileId = r.RiskProfileId,
            GovernorEnabled = r.GovernorEnabled,
            RegimeEnabled = r.RegimeEnabled,
            CommissionPerMillion = r.CommissionPerMillion,
            SpreadPips = r.SpreadPips,
            WallElapsedMs = r.WallElapsedMs,
            BarsPerSec = r.BarsPerSec,
            TotalBars = r.TotalBars,
        };
    }

    private RunDetailResponse BuildRunDetailFromState(BacktestOrchestrator.BacktestRunState state)
    {
        var wallElapsedMs = (long)(DateTime.UtcNow - state.StartedAt).TotalMilliseconds;
        var barsPerSec = wallElapsedMs > 0 ? state.BarCount / (wallElapsedMs / 1000.0) : 0;

        int totalTrades = 0, winningTrades = 0;
        decimal netProfit = 0, grossPnL = 0, commissionTotal = 0, swapTotal = 0;
        double winRatePct = 0;

        if (_cache is not null && _cache.HasRun(state.RunId))
        {
            var trades = _cache.GetTrades(state.RunId);
            if (trades.Count > 0)
            {
                totalTrades = trades.Count;
                winningTrades = trades.Count(t => t.NetPnL.Amount > 0);
                winRatePct = totalTrades > 0 ? (double)winningTrades / totalTrades : 0;
                netProfit = trades.Sum(t => t.NetPnL.Amount);
                grossPnL = trades.Sum(t => t.GrossPnL.Amount);
                commissionTotal = trades.Sum(t => t.Commission.Amount);
                swapTotal = trades.Sum(t => t.Swap.Amount);
            }
        }

        return new RunDetailResponse
        {
            RunId = state.RunId,
            Status = state.Status,
            Symbol = state.Symbol,
            Period = state.Period,
            Symbols = state.Symbol,
            Periods = state.Period,
            StartedAtUtc = state.StartedAt,
            CompletedAtUtc = null,
            BacktestFrom = state.BacktestFrom == default ? state.StartedAt : state.BacktestFrom,
            BacktestTo = state.BacktestTo,
            InitialBalance = state.InitialBalance,
            NetProfit = netProfit,
            GrossPnL = grossPnL,
            CommissionTotal = commissionTotal,
            SwapTotal = swapTotal,
            MaxDrawdownPct = state.MaxDdPct,
            TotalTrades = totalTrades,
            WinningTrades = winningTrades,
            WinRatePct = winRatePct,
            ErrorMessage = state.Error,
            ExitCode = -1,
            WallElapsedMs = wallElapsedMs,
            BarsPerSec = barsPerSec,
            TotalBars = state.BarsTotal,
            Venue = state.Venue,
            RiskProfileId = state.RiskProfileId,
            GovernorEnabled = state.GovernorEnabled,
            RegimeEnabled = state.RegimeEnabled,
            CommissionPerMillion = state.CommissionPerMillion,
            SpreadPips = state.SpreadPips,
            ExitResolution = state.ExitResolution,
            EffectiveConfigJson = state.EffectiveConfigJson,
            RunPlanJson = state.RunPlanJson ?? "[]",
        };
    }

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
    /// The prop-firm reset-period date a UTC instant belongs to (default 22:00 UTC roll — matches
    /// <c>ResetClock.ResetPeriodDate</c> in TradingEngine.Host and every seeded ruleset's
    /// <c>dailyResetTimeUtc</c>). Before 22:00 you're still in yesterday's period. Daily PnL/DD MUST bucket
    /// on this boundary, not calendar midnight — see iter-merge-plan PLAN.md "What NOT to do".
    /// </summary>
    private static DateOnly PropFirmDayOf(DateTime closedAtUtc)
    {
        var date = DateOnly.FromDateTime(closedAtUtc);
        return TimeOnly.FromDateTime(closedAtUtc) >= new TimeOnly(22, 0) ? date : date.AddDays(-1);
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

        if (trades.Count == 0) return null;

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

    public async Task<IReadOnlyList<BarNarrativeResponse>> GetRunBarsAsync(
        string runId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        if (_cache is not null && _cache.HasRun(runId))
        {
            var cachedJournal = _cache.GetJournal(runId, MaxBarEvents);
            if (cachedJournal.Count > 0)
                return BuildBarNarratives(cachedJournal, from, to);
        }

        var bars = new Dictionary<DateTime, List<StepRecord>>();
        var eventCount = 0;

        await foreach (var record in _journals.StreamByRunAsync(runId, null, ct))
        {
            if (eventCount >= MaxBarEvents) break;
            if (from.HasValue && record.SimTimeUtc < from.Value) continue;
            if (to.HasValue && record.SimTimeUtc > to.Value) continue;

            if (!bars.TryGetValue(record.SimTimeUtc, out var group))
            {
                group = [];
                bars[record.SimTimeUtc] = group;
            }
            group.Add(record);
            eventCount++;
        }

        var allRecords = bars.Values.SelectMany(g => g).ToList();
        return BuildBarNarratives(allRecords, from, to);
    }

    private static IReadOnlyList<BarNarrativeResponse> BuildBarNarratives(
        IReadOnlyList<StepRecord> records, DateTime? from, DateTime? to)
    {
        var bars = new Dictionary<DateTime, List<StepRecord>>();
        foreach (var record in records)
        {
            if (from.HasValue && record.SimTimeUtc < from.Value) continue;
            if (to.HasValue && record.SimTimeUtc > to.Value) continue;

            if (!bars.TryGetValue(record.SimTimeUtc, out var group))
            {
                group = [];
                bars[record.SimTimeUtc] = group;
            }
            group.Add(record);
        }

        var result = new List<BarNarrativeResponse>(bars.Count);
        foreach (var (simTime, group) in bars.OrderBy(kv => kv.Key))
        {
            var first = group[0];
            var barClosed = group.FirstOrDefault(r => r.EventKind == "BarClosed");
            var last = group[^1];

            result.Add(new BarNarrativeResponse
            {
                SimTimeUtc = simTime,
                FirstSeq = first.Seq,
                EventCount = group.Count,
                Regime = barClosed?.Regime ?? group.FirstOrDefault(r => r.Regime != null)?.Regime,
                Verdicts = (barClosed?.StrategyVerdicts ?? [])
                    .Select(v => new BarStrategyVerdictDto
                    {
                        StrategyId = v.StrategyId,
                        SignalFired = v.SignalFired,
                        Direction = v.Direction?.ToString() ?? (v.SignalFired ? "Long" : null),
                        Reason = v.Reason,
                    }).ToList(),
                ProposalCount = group.Count(r => r.EventKind == "OrderProposed"),
                GateRejections = group
                    .Where(r => r.DecisionReason is not null && IsRejection(r.DecisionReason))
                    .Select(r => r.DecisionReason!)
                    .Distinct()
                    .ToList(),
                Risk = last.Risk is { } risk ? new BarRiskSnapshotDto
                {
                    Equity = risk.Equity,
                    Balance = risk.Balance,
                    DailyDrawdown = risk.DailyDrawdown,
                    MaxDrawdown = risk.MaxDrawdown,
                    OpenPositions = risk.OpenPositions,
                    InProtectionMode = risk.InProtectionMode,
                    GovernorState = risk.GovernorState,
                } : null,
                FillCount = group.Count(r => r.EventKind == "OrderFilled"),
                CloseCount = group.Count(r => r.EffectKinds.Any(e => e == "PublishTradeClosed")),
                RejectionCount = group.Count(r => r.DecisionReason is not null && IsRejection(r.DecisionReason)),
            });
        }

        return result;
    }

    private static bool IsRejection(string reason) =>
        !reason.Equals("Accepted", StringComparison.Ordinal) &&
        !reason.Equals("Filled", StringComparison.Ordinal) &&
        !reason.Equals("BarUpdate", StringComparison.Ordinal) &&
        !reason.Equals("TickUpdate", StringComparison.Ordinal) &&
        !reason.Equals("PartialFill", StringComparison.Ordinal) &&
        !reason.Equals("PartialClose", StringComparison.Ordinal) &&
        !reason.Equals("StillReducing", StringComparison.Ordinal) &&
        !reason.Equals("PartialCloseWhileClosing", StringComparison.Ordinal);

    private static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(30);

    private void FixStuckRunStatuses(List<RunListResponse> runs)
    {
        for (var i = 0; i < runs.Count; i++)
        {
            var r = runs[i];
            if (r.Status == "running" && r.CompletedAtUtc is null
                && DateTime.UtcNow - r.StartedAtUtc > StuckThreshold
                && (_orchestrator?.GetState(r.RunId) is null))
            {
                runs[i] = r with { Status = "failed", ErrorMessage = (r.ErrorMessage ?? "") + " Timed out (stuck)." };
            }
        }
    }

    private static string ResolveStatus(BacktestRunSummary r, BacktestOrchestrator? orchestrator)
    {
        if (r.CompletedAtUtc != default)
        {
            return r.ErrorMessage != null ? "failed" : "completed";
        }

        if (DateTime.UtcNow - r.StartedAtUtc > StuckThreshold
            && (orchestrator?.GetState(r.RunId) is null))
        {
            return "failed";
        }

        return "running";
    }
}

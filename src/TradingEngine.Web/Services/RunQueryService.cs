using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Web.Dtos.Runs;

namespace TradingEngine.Web.Services;

public sealed class RunQueryService : IRunQueryService
{
    private readonly TradingDbContext _db;
    private readonly IBacktestRunRepository _runRepo;
    private readonly IEquityRepository _equityRepo;
    private readonly IJournalQueryRepository _journals;

    public RunQueryService(
        TradingDbContext db,
        IBacktestRunRepository runRepo,
        IEquityRepository equityRepo,
        IJournalQueryRepository journals)
    {
        _db = db;
        _runRepo = runRepo;
        _equityRepo = equityRepo;
        _journals = journals;
    }

    public async Task<IReadOnlyList<RunListResponse>> GetRunsAsync(CancellationToken ct)
    {
        var runs = await _runRepo.GetAllAsync(ct);
        var createdMap = await _db.BacktestRuns
            .Select(x => new { x.RunId, x.CreatedAtUtc })
            .ToDictionaryAsync(x => x.RunId, x => x.CreatedAtUtc, ct);
        return runs.Take(50).Select(r => new RunListResponse
        {
            RunId = r.RunId,
            CreatedAtUtc = createdMap.GetValueOrDefault(r.RunId),
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
        }).ToList();
    }

    public async Task<RunDetailResponse?> GetRunAsync(string runId, CancellationToken ct)
    {
        var r = await _runRepo.GetByIdAsync(runId, ct);
        if (r is null) return null;

        return new RunDetailResponse
        {
            RunId = r.RunId,
            Status = r.CompletedAtUtc == default ? "running"
                : r.ErrorMessage != null ? "failed"
                : "completed",
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
        };
    }

    public async Task<IReadOnlyList<TradeSummaryResponse>> GetRunTradesAsync(string runId, CancellationToken ct)
    {
        return await _db.Trades
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
                EntryType = t.Mode,
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<EquityPointResponse>> GetRunEquityAsync(string runId, CancellationToken ct)
    {
        var snapshots = await _equityRepo.GetByRunIdAsync(runId, ct);
        return snapshots.Select(s => new EquityPointResponse
        {
            TimestampUtc = s.TimestampUtc,
            Equity = s.Equity,
            Balance = s.Balance,
        }).ToList();
    }

    public async Task<IReadOnlyList<DailyPnlResponse>> GetRunDailyPnLAsync(string runId, CancellationToken ct)
    {
        var trades = await _db.Trades
            .Where(t => t.RunId == runId)
            .OrderBy(t => t.ClosedAtUtc)
            .ToListAsync(ct);

        return trades.GroupBy(t => t.ClosedAtUtc.Date)
            .Select(g => new DailyPnlResponse
            {
                Date = g.Key.ToString("yyyy-MM-dd"),
                PnL = g.Sum(t => t.NetPnLAmount),
            })
            .ToList();
    }

    public async Task<RunAnalyticsResponse?> GetRunAnalyticsAsync(string runId, CancellationToken ct)
    {
        var trades = await _db.Trades
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
        var bars = new Dictionary<DateTime, List<StepRecord>>();

        await foreach (var record in _journals.StreamByRunAsync(runId, null, ct))
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
}

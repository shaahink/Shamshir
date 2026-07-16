using TradingEngine.Web.Dtos.Runs;

namespace TradingEngine.Web.Services;

/// <summary>The single-run detail read path: live in-memory state while the run is in flight
/// (with cache-backed trade economics), the DB record once terminal. Extracted verbatim from
/// RunQueryService.</summary>
public sealed class RunDetailQuery(
    IBacktestRunRepository runRepo,
    IRunDataCache? runDataCache,
    ILiveRunReader? liveRuns)
{
    private readonly IBacktestRunRepository _runRepo = runRepo;
    private readonly IRunDataCache? _cache = runDataCache;
    private readonly ILiveRunReader? _live = liveRuns;

    public async Task<RunDetailResponse?> GetRunAsync(string runId, CancellationToken ct)
    {
        if (_live is not null)
        {
            var state = _live.GetState(runId);
            if (state is not null && state.Status is not "completed" and not "failed" and not "cancelled")
            {
                return BuildRunDetailFromState(state);
            }
        }

        var r = await _runRepo.GetByIdAsync(runId, ct);
        if (r is null)
        {
            return null;
        }

        return new RunDetailResponse
        {
            RunId = r.RunId,
            Status = RunStatusOverlay.ResolveStatus(r, _live),
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
            WarningsJson = r.WarningsJson,
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
            ParentRunId = r.ParentRunId,
            ComparePairId = r.ComparePairId,
            ExplorationMode = r.ExplorationMode,
            RecordExcursions = r.RecordExcursions,
            Notes = r.Notes,
        };
    }

    private RunDetailResponse BuildRunDetailFromState(BacktestRunState state)
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
            ExplorationMode = state.ExplorationMode,
            RecordExcursions = state.RecordExcursions,
        };
    }
}

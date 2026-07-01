namespace TradingEngine.Web.Api;

using TradingEngine.Risk.Compliance;

/// <summary>
/// Legacy analytics controller. The SPA now uses <c>/api/runs/{id}/...</c> endpoints exclusively
/// (see <see cref="RunsController"/>). These endpoints survive for backward compatibility with
/// external callers / curl / manual testing, but are NOT consumed by the Angular SPA.
/// </summary>
[ApiController]
[Route("api/backtest/analytics")]
public class BacktestAnalyticsController : ControllerBase
{
    private readonly TradingDbContext _db;
    private readonly IPassProbabilityEstimator _estimator;
    private readonly IBacktestRunRepository _runRepo;
    private readonly IStrategyConfigStore _strategyConfigStore;
    private readonly IRiskProfileStore _riskProfileStore;
    private readonly IPropFirmRuleSetStore _propFirmStore;

    public BacktestAnalyticsController(
        TradingDbContext db,
        IPassProbabilityEstimator estimator,
        IBacktestRunRepository runRepo,
        IStrategyConfigStore strategyConfigStore,
        IRiskProfileStore riskProfileStore,
        IPropFirmRuleSetStore propFirmStore)
    {
        _db = db;
        _estimator = estimator;
        _runRepo = runRepo;
        _strategyConfigStore = strategyConfigStore;
        _riskProfileStore = riskProfileStore;
        _propFirmStore = propFirmStore;
    }

    // iter-38 W-B4: resolve the prop-firm ruleset the engine actually runs under, mirroring
    // EngineHostFactory.WireRiskRules (active strategy's RiskProfileId → profile's PropFirmRuleSetId →
    // ruleset). Pass-probability was hardcoding FTMO 10/5/10 and ignoring the configured ruleset.
    private async Task<PropFirmRuleSet?> ResolveActiveRuleSetAsync(CancellationToken ct)
    {
        var configs = await _strategyConfigStore.GetAllAsync(ct);
        var activeProfileId =
            configs.Where(c => c.Enabled).Select(c => c.RiskProfileId).FirstOrDefault()
            ?? configs.Select(c => c.RiskProfileId).FirstOrDefault()
            ?? "standard";
        var profiles = await _riskProfileStore.GetAllAsync(ct);
        var activeRuleSetId = profiles.FirstOrDefault(p => p.Id == activeProfileId)?.PropFirmRuleSetId
            ?? "ftmo-standard";
        var ruleSets = await _propFirmStore.GetAllAsync(ct);
        return ruleSets.FirstOrDefault(r => r.Id == activeRuleSetId);
    }

    [HttpGet("runs")]
    public async Task<IActionResult> GetRuns()
    {
        // Via the repository so interrupted-run summaries are self-healed from their trades
        // (otherwise this list shows "0 trades / running" for runs that clearly have trades).
        var runs = await _runRepo.GetAllAsync(HttpContext.RequestAborted);
        var result = runs.Take(50).Select(r => new
        {
            r.RunId,
            status = r.CompletedAtUtc == default ? "running" : r.ErrorMessage == null ? "completed" : "failed",
            r.NetProfit,
            r.MaxDrawdownPct,
            r.TotalTrades,
            r.WinningTrades,
            r.WinRatePct,
            r.StartedAtUtc,
            CompletedAtUtc = r.CompletedAtUtc,
        }).ToList();
        return Ok(result);
    }

    [HttpGet("{runId}/pass-probability")]
    public async Task<IActionResult> GetPassProbability(string runId)
    {
        var trades = await _db.Trades.Where(t => t.RunId == runId).OrderBy(t => t.ClosedAtUtc).ToListAsync();
        var dailyPnL = trades.GroupBy(t => t.ClosedAtUtc.Date)
            .Select(g => g.Sum(t => t.NetPnLAmount))
            .Select(d => (decimal)d)
            .ToList();

        var run = await _db.BacktestRuns.FirstOrDefaultAsync(r => r.RunId == runId);
        var initialBalance = run?.InitialBalance ?? 100_000m;
        var currentEquity = initialBalance + dailyPnL.Sum();

        // iter-38 W-B4: pull the targets/limits from the configured ruleset (FTMO-ish defaults only as a
        // fallback when no ruleset resolves). DaysRemaining keeps the 30-day assumption — the ruleset has no
        // total challenge-length field (MinTradingDays is a floor, not the window).
        var ruleSet = await ResolveActiveRuleSetAsync(HttpContext.RequestAborted);

        var input = new PassProbabilityInput
        {
            CurrentEquity = currentEquity,
            InitialBalance = initialBalance,
            ProfitTargetPercent = ruleSet?.ProfitTargetPercent ?? 0.10,
            MaxDailyLossPercent = ruleSet?.MaxDailyLossPercent ?? 0.05,
            MaxTotalLossPercent = ruleSet?.MaxTotalLossPercent ?? 0.10,
            DaysRemaining = Math.Max(1, 30 - dailyPnL.Count),
            HistoricalDailyPnL = dailyPnL,
            MonteCarloRuns = 10_000,
        };
        return Ok(_estimator.Estimate(input));
    }

    [HttpGet("compare")]
    public async Task<IActionResult> Compare([FromQuery] string runIds)
    {
        var ids = (runIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<object>();
        foreach (var id in ids)
        {
            var run = await _runRepo.GetByIdAsync(id, HttpContext.RequestAborted);
            if (run is null) continue;
            results.Add(new { run.RunId, run.NetProfit, run.MaxDrawdownPct, run.TotalTrades, run.WinningTrades, run.WinRatePct });
        }
        return Ok(results);
    }

    [HttpGet("{runId}/daily-pnl")]
    public async Task<IActionResult> GetDailyPnL(string runId)
    {
        var trades = await _db.Trades.Where(t => t.RunId == runId).OrderBy(t => t.ClosedAtUtc).ToListAsync();
        // iter-38 W-B9/W-B10: daily buckets are UTC calendar dates — grouped off ClosedAtUtc (stored UTC;
        // SQLite materializes the same wall-clock with Kind=Unspecified). The W-B8 converter emits 'Z' so the
        // client localizes for display; the server-side buckets stay deterministic UTC.
        var daily = trades.GroupBy(t => t.ClosedAtUtc.Date)
            .Select(g => new { date = g.Key.ToString("yyyy-MM-dd"), pnl = g.Sum(t => t.NetPnLAmount) })
            .ToList();
        return Ok(daily);
    }

    [HttpGet("{runId}/analytics")]
    public async Task<IActionResult> GetAnalytics(string runId)
    {
        var trades = await _db.Trades.Where(t => t.RunId == runId).OrderBy(t => t.ClosedAtUtc).ToListAsync();
        if (trades.Count == 0) return Ok(new { rMultiples = Array.Empty<double>(), holdingTimes = Array.Empty<double>(), pnlByHour = Array.Empty<object>(), pnlByDay = Array.Empty<object>(), maeMfe = Array.Empty<object>() });

        var rMultiples = trades.Select(t => t.RMultiple).ToList();
        // iter-38 W-B3: report the real holding time (was clamped at 3600s, so every trade held >1h showed as
        // exactly 60min — a misleading distribution).
        var holdingTimes = trades.Select(t => t.DurationSeconds).ToList();
        // iter-38 W-B5: keep PnL in decimal (repo money rule) instead of casting to double per bucket.
        // iter-38 W-B9/W-B10: pnl-by-hour/-day are UTC buckets (ClosedAtUtc is stored UTC and materialized
        // faithfully by SQLite) — a deliberate UTC convention, not a defect.
        var pnlByHour = trades.GroupBy(t => t.ClosedAtUtc.Hour)
            .Select(g => new { key = g.Key, value = g.Sum(t => t.NetPnLAmount) }).ToList();
        var pnlByDay = trades.GroupBy(t => t.ClosedAtUtc.DayOfWeek)
            .Select(g => new { key = g.Key.ToString(), value = g.Sum(t => t.NetPnLAmount) }).ToList();
        var maeMfe = trades.Select(t => new { x = -t.MaxAdverseExcursion, y = t.MaxFavorableExcursion }).ToList();

        return Ok(new { rMultiples, holdingTimes, pnlByHour, pnlByDay, maeMfe });
    }

    [HttpGet("analytics/correlation")]
    public async Task<IActionResult> GetCorrelation([FromQuery] string symbols, [FromQuery] int days = 90)
    {
        var symList = (symbols ?? "EURUSD,GBPUSD").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var from = DateTime.UtcNow.AddDays(-days);
        var matrix = new List<List<double>>();

        foreach (var s1 in symList)
        {
            var row = new List<double>();
            var closes1 = await _db.Bars.Where(b => b.Symbol == s1 && b.OpenTimeUtc >= from).OrderBy(b => b.OpenTimeUtc).Select(b => b.Close).ToListAsync();
            foreach (var s2 in symList)
            {
                var closes2 = await _db.Bars.Where(b => b.Symbol == s2 && b.OpenTimeUtc >= from).OrderBy(b => b.OpenTimeUtc).Select(b => b.Close).ToListAsync();
                row.Add(PearsonR(closes1, closes2));
            }
            matrix.Add(row);
        }
        return Ok(new { symbols = symList, matrix });
    }

    private static double PearsonR(List<decimal> a, List<decimal> b)
    {
        var n = Math.Min(a.Count, b.Count);
        if (n < 2) return 0;
        var ax = a.Take(n).Select(x => (double)x).ToArray();
        var bx = b.Take(n).Select(x => (double)x).ToArray();
        var avgA = ax.Average(); var avgB = bx.Average();
        var num = 0.0; var denA = 0.0; var denB = 0.0;
        for (int i = 0; i < n; i++)
        {
            var da = ax[i] - avgA; var db = bx[i] - avgB;
            num += da * db; denA += da * da; denB += db * db;
        }
        var den = Math.Sqrt(denA * denB);
        return den > 0 ? num / den : 0;
    }
}

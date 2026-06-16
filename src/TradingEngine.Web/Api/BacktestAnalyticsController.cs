namespace TradingEngine.Web.Api;

using TradingEngine.Risk.Compliance;

[ApiController]
[Route("api/backtest")]
public class BacktestAnalyticsController : ControllerBase
{
    private readonly TradingDbContext _db;
    private readonly IPassProbabilityEstimator _estimator;
    private readonly IBacktestRunRepository _runRepo;

    public BacktestAnalyticsController(TradingDbContext db, IPassProbabilityEstimator estimator, IBacktestRunRepository runRepo)
    {
        _db = db;
        _estimator = estimator;
        _runRepo = runRepo;
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

        var input = new PassProbabilityInput
        {
            CurrentEquity = currentEquity,
            InitialBalance = initialBalance,
            ProfitTargetPercent = 0.10,
            MaxDailyLossPercent = 0.05,
            MaxTotalLossPercent = 0.10,
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
        var holdingTimes = trades.Select(t => Math.Min(t.DurationSeconds, 3600)).ToList();
        var pnlByHour = trades.GroupBy(t => t.ClosedAtUtc.Hour)
            .Select(g => new { key = g.Key, value = g.Sum(t => (double)t.NetPnLAmount) }).ToList();
        var pnlByDay = trades.GroupBy(t => t.ClosedAtUtc.DayOfWeek)
            .Select(g => new { key = g.Key.ToString(), value = g.Sum(t => (double)t.NetPnLAmount) }).ToList();
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

    [HttpGet("analytics/regime-history")]
    public async Task<IActionResult> GetRegimeHistory([FromQuery] string symbol, [FromQuery] int days = 30)
    {
        var from = DateTime.UtcNow.AddDays(-days);
        var bars = await _db.Bars.Where(b => b.Symbol == symbol && b.OpenTimeUtc >= from).OrderBy(b => b.OpenTimeUtc).ToListAsync();
        var result = bars.Select(b => new { date = b.OpenTimeUtc.ToString("yyyy-MM-dd"), regime = "Unknown" }).ToList();
        return Ok(result);
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

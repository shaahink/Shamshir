namespace TradingEngine.Web.Api;

using TradingEngine.Risk.Compliance;
using TradingEngine.Web.Services;

[ApiController]
[Route("api/backtest")]
public class BacktestAnalyticsController : ControllerBase
{
    private readonly IBacktestQueryService _query;
    private readonly TradingDbContext _db;

    public BacktestAnalyticsController(IBacktestQueryService query, TradingDbContext db)
    {
        _query = query;
        _db = db;
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

        var estimator = new PassProbabilityEstimator();
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
        var result = estimator.Estimate(input);
        return Ok(result);
    }

    [HttpGet("compare")]
    public async Task<IActionResult> Compare([FromQuery] string runIds)
    {
        var ids = (runIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<object>();
        foreach (var id in ids)
        {
            var run = await _db.BacktestRuns.FirstOrDefaultAsync(r => r.RunId == id);
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

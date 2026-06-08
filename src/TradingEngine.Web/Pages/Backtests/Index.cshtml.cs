using TradingEngine.CTraderRunner;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Pages.Backtests;

public sealed class IndexModel : PageModel
{
    private readonly BacktestOrchestrator _orchestrator;
    private readonly IBacktestQueryService _query;

    public IReadOnlyList<BacktestOrchestrator.BacktestRunState> Runs { get; private set; } = [];

    public IndexModel(BacktestOrchestrator orchestrator, IBacktestQueryService query)
    {
        _orchestrator = orchestrator;
        _query = query;
    }

    public async Task OnGet()
    {
        var activeRuns = _orchestrator.GetAll();
        var activeIds = new HashSet<string>(activeRuns.Select(r => r.RunId));

        var persisted = await _query.GetAllRunsAsync(HttpContext.RequestAborted);

        var merged = new List<BacktestOrchestrator.BacktestRunState>();
        merged.AddRange(activeRuns);

        foreach (var p in persisted)
        {
            if (activeIds.Contains(p.RunId)) continue;
            merged.Add(new BacktestOrchestrator.BacktestRunState
            {
                RunId = p.RunId,
                Symbol = p.Symbol,
                Period = p.Period,
                StartedAt = p.StartedAt,
                Status = p.Status,
                Result = new BacktestResult
                {
                    RunId = p.RunId,
                    NetProfit = p.NetProfit,
                    MaxDrawdownPct = p.MaxDrawdownPct,
                    TotalTrades = p.TotalTrades,
                    WinningTrades = p.WinningTrades,
                    WinRatePct = p.WinRatePct,
                },
            });
        }

        Runs = merged;
    }
}

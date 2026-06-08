using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Pages.Backtests;

public sealed class IndexModel : PageModel
{
    private readonly BacktestOrchestrator _orchestrator;
    private readonly IBacktestRunRepository _repo;

    public IReadOnlyList<BacktestOrchestrator.BacktestRunState> Runs { get; private set; } = [];
    public int AllTimeTrades { get; private set; }
    public decimal AllTimePnL { get; private set; }

    public IndexModel(BacktestOrchestrator orchestrator, IBacktestRunRepository repo)
    {
        _orchestrator = orchestrator;
        _repo = repo;
    }

    public async Task OnGet()
    {
        var activeRuns = _orchestrator.GetAll();
        var activeIds = new HashSet<string>(activeRuns.Select(r => r.RunId));

        var persisted = await _repo.GetAllAsync(HttpContext.RequestAborted);

        var merged = new List<BacktestOrchestrator.BacktestRunState>();
        merged.AddRange(activeRuns);

        foreach (var p in persisted)
        {
            if (activeIds.Contains(p.RunId)) continue;
            merged.Add(new BacktestOrchestrator.BacktestRunState
            {
                RunId = p.RunId,
                Symbol = p.Symbol,
                Period = "",
                StartedAt = p.StartedAtUtc,
                Status = "completed",
                Result = new TradingEngine.CTraderRunner.BacktestResult
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

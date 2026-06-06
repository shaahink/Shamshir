using TradingEngine.Web.Services;

namespace TradingEngine.Web.Pages.Backtests;

public sealed class IndexModel : PageModel
{
    private readonly BacktestOrchestrator _orchestrator;

    public IReadOnlyList<BacktestOrchestrator.BacktestRunState> Runs { get; private set; } = [];

    public IndexModel(BacktestOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public void OnGet()
    {
        Runs = _orchestrator.GetAll();
    }
}

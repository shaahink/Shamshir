using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingEngine.CTraderRunner;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Pages.Backtests;

public sealed class ProgressModel : PageModel
{
    private readonly BacktestOrchestrator _orchestrator;

    public string RunId { get; private set; } = "";
    public BacktestOrchestrator.BacktestRunState? State { get; private set; }

    public ProgressModel(BacktestOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public IActionResult OnGet(string runId)
    {
        RunId = runId;
        State = _orchestrator.GetState(runId);
        if (State is null)
            return NotFound();
        return Page();
    }
}

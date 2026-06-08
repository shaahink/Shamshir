using TradingEngine.Web.Services;

namespace TradingEngine.Web.Pages.Backtests;

public sealed class DetailModel : PageModel
{
    private readonly IBacktestQueryService _query;
    private readonly BacktestOrchestrator _orchestrator;

    public BacktestRunView? Run { get; private set; }
    public bool IsActive { get; private set; }

    public DetailModel(IBacktestQueryService query, BacktestOrchestrator orchestrator)
    {
        _query = query;
        _orchestrator = orchestrator;
    }

    public async Task OnGet(string runId)
    {
        var state = _orchestrator.GetState(runId);
        if (state is not null)
        {
            IsActive = true;
            Run = new BacktestRunView(
                state.RunId, state.StartedAt, state.Status,
                state.Symbol, state.Period, DateTime.MinValue, DateTime.MinValue,
                0, 0, 0, 0, 0, 0, "", state.Error);
            return;
        }

        Run = await _query.GetRunAsync(runId, HttpContext.RequestAborted);
        IsActive = false;
    }
}

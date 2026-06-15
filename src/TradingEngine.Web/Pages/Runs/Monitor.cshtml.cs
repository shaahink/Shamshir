namespace TradingEngine.Web.Pages.Runs;

public sealed class MonitorModel : Microsoft.AspNetCore.Mvc.RazorPages.PageModel
{
    public string RunId { get; set; } = "";

    public void OnGet(string runId)
    {
        RunId = runId;
    }
}

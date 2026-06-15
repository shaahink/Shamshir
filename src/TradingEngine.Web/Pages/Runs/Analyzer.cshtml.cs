using System.Text.Json;

namespace TradingEngine.Web.Pages.Runs;

public sealed class AnalyzerModel : PageModel
{
    public string RunId { get; set; } = "";

    public void OnGet(string runId)
    {
        RunId = runId;
    }
}

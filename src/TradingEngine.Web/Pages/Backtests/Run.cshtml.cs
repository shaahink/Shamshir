using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradingEngine.CTraderRunner;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Pages.Backtests;

public sealed class RunModel : PageModel
{
    private readonly IBacktestCommandService _command;
    private readonly IConfiguration _config;

    [BindProperty]
    public string Symbol { get; set; } = "EURUSD";

    [BindProperty]
    public string Period { get; set; } = "h1";

    [BindProperty]
    public DateTime StartDate { get; set; } = new(2024, 1, 15);

    [BindProperty]
    public DateTime EndDate { get; set; } = new(2024, 4, 15);

    [BindProperty]
    public decimal Balance { get; set; } = 100_000;

    public string? RunId { get; set; }
    public bool CredentialsConfigured { get; set; } = true;

    public RunModel(IBacktestCommandService command, IConfiguration config)
    {
        _command = command;
        _config = config;
    }

    public void OnGet()
    {
        CredentialsConfigured = !string.IsNullOrWhiteSpace(_config["CTrader:CtId"])
            && !string.IsNullOrWhiteSpace(_config["CTrader:PwdFile"])
            && !string.IsNullOrWhiteSpace(_config["CTrader:Account"]);
    }

    public async Task<IActionResult> OnPost()
    {
        var cfg = new BacktestConfig
        {
            Symbol = Symbol.ToUpperInvariant(),
            Period = Period.ToLowerInvariant(),
            Start = StartDate,
            End = EndDate,
            Balance = Balance,
        };

        var runId = await _command.StartAsync(cfg, HttpContext.RequestAborted);
        return RedirectToPage("Progress", new { runId });
    }
}

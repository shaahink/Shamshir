namespace TradingEngine.Web.Pages;

public sealed class IndexModel : PageModel
{
    public RiskState? RiskState { get; private set; }
    public int OpenPositionCount { get; private set; }

    public void OnGet()
    {
        RiskState = new RiskState(true, false, null, 0, 0, 0.05m, 0.10m, null);
        OpenPositionCount = 0;
    }
}

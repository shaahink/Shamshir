namespace TradingEngine.Web.Pages;

public sealed class PerformanceModel : PageModel
{
    public int TotalTrades { get; private set; }
    public double WinRate { get; private set; }
    public decimal NetPnL { get; private set; }
    public double AvgHoldHours { get; private set; }

    public void OnGet()
    {
        TotalTrades = 0;
        WinRate = 0;
        NetPnL = 0;
        AvgHoldHours = 0;
    }
}

namespace TradingEngine.Web.Pages;

public sealed class PerformanceModel(TradeReportQueries queries) : PageModel
{
    public int TotalTrades { get; private set; }
    public double WinRate { get; private set; }
    public decimal NetPnL { get; private set; }
    public double AvgHoldHours { get; private set; }

    public async void OnGet()
    {
        try
        {
            var summary = await queries.GetSummaryAsync(
                DateTime.MinValue, DateTime.MaxValue, null, HttpContext.RequestAborted);
            TotalTrades = summary.TotalTrades;
            WinRate = summary.TotalTrades > 0 ? (double)summary.Wins / summary.TotalTrades : 0;
            NetPnL = summary.TotalNetPnL;
            AvgHoldHours = summary.AvgHoldHours;
        }
        catch
        {
            TotalTrades = 0;
        }
    }
}

namespace TradingEngine.Web.Pages;

public sealed class IndexModel(ReportingDbContext db) : PageModel
{
    public RiskState? RiskState { get; private set; }
    public int OpenPositionCount { get; private set; }
    public decimal LatestEquity { get; private set; }
    public int TotalTradesToday { get; private set; }

    public async void OnGet()
    {
        var latest = await db.EquitySnapshots.OrderByDescending(e => e.TimestampUtc).FirstOrDefaultAsync();
        if (latest is not null)
        {
            LatestEquity = latest.Equity;
            RiskState = new RiskState(
                true, false, null,
                latest.CurrentDailyDrawdown, latest.CurrentMaxDrawdown,
                0.05m, 0.10m, null);
        }

        var todayStart = DateTime.UtcNow.Date;
        TotalTradesToday = await db.Trades.CountAsync(t => t.ClosedAtUtc >= todayStart);
    }
}

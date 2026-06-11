namespace TradingEngine.Web.Pages;

public sealed class IndexModel(ReportingDbContext db) : PageModel
{
    public ExtendedRiskState? RiskState { get; private set; }
    public int OpenPositionCount { get; private set; }
    public decimal LatestEquity { get; private set; }
    public int TotalTradesToday { get; private set; }

    public async Task OnGet()
    {
        var latest = await db.EquitySnapshots.OrderByDescending(e => e.TimestampUtc).FirstOrDefaultAsync();
        if (latest is not null)
        {
            LatestEquity = latest.Equity;
            RiskState = new ExtendedRiskState
            {
                TradingAllowed = true,
                DailyDrawdownUsed = latest.CurrentDailyDrawdown,
                MaxDrawdownUsed = latest.CurrentMaxDrawdown,
                DailyDrawdownLimit = 0.05m,
                MaxDrawdownLimit = 0.10m,
            };
        }

        var todayStart = DateTime.UtcNow.Date;
        TotalTradesToday = await db.Trades.CountAsync(t => t.ClosedAtUtc >= todayStart);
    }
}

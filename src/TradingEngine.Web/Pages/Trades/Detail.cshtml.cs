namespace TradingEngine.Web.Pages.Trades;

public sealed class TradeDetailModel(ReportingDbContext db) : PageModel
{
    public TradeResultEntity? Trade { get; private set; }

    public async Task OnGet(Guid id)
    {
        Trade = await db.Trades.FindAsync(id);
    }
}

namespace TradingEngine.Web.Pages.Trades;

public sealed class TradesIndexModel(ReportingDbContext db) : PageModel
{
    public IReadOnlyList<TradeResultEntity> Trades { get; private set; } = [];
    public int TotalPages { get; private set; }
    public int CurrentPage { get; private set; }

    public async Task OnGet(int page = 1, string? strategyId = null)
    {
        CurrentPage = page;
        var pageSize = 50;
        var query = db.Trades.AsQueryable();
        if (!string.IsNullOrEmpty(strategyId))
            query = query.Where(t => t.StrategyId == strategyId);

        var total = await query.CountAsync();
        TotalPages = (int)Math.Ceiling((double)total / pageSize);

        Trades = await query.OrderByDescending(t => t.ClosedAtUtc)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
    }
}

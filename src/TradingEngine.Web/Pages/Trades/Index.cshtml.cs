using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Web.Pages.Trades;

public sealed class TradesIndexModel(ReportingDbContext db) : PageModel
{
    public IReadOnlyList<TradeResultEntity> Trades { get; private set; } = [];
    public int TotalCount { get; private set; }
    public int TotalPages { get; private set; }
    public int CurrentPage { get; private set; } = 1;
    public int PageSize { get; private set; } = 50;

    [BindProperty(SupportsGet = true)]
    public string? RunId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Symbol { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? StrategyId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Direction { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? FromDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? ToDate { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? WinLoss { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Sort { get; set; }

    public string[] AllSymbols { get; private set; } = [];
    public string[] AllStrategyIds { get; private set; } = [];
    public string[] AllRunIds { get; private set; } = [];

    public async Task OnGet(int page = 1, int pageSize = 50)
    {
        CurrentPage = Math.Max(1, page);
        PageSize = Math.Clamp(pageSize, 10, 200);

        var query = db.Trades.AsQueryable();

        if (!string.IsNullOrEmpty(RunId))
            query = query.Where(t => t.RunId == RunId);
        if (!string.IsNullOrEmpty(Symbol))
            query = query.Where(t => t.Symbol == Symbol);
        if (!string.IsNullOrEmpty(StrategyId))
            query = query.Where(t => t.StrategyId == StrategyId);
        if (!string.IsNullOrEmpty(Direction))
            query = query.Where(t => t.Direction == Direction);
        if (FromDate.HasValue)
            query = query.Where(t => t.ClosedAtUtc >= FromDate.Value);
        if (ToDate.HasValue)
            query = query.Where(t => t.ClosedAtUtc <= ToDate.Value);
        if (WinLoss == "win")
            query = query.Where(t => t.NetPnLAmount > 0);
        else if (WinLoss == "loss")
            query = query.Where(t => t.NetPnLAmount < 0);

        TotalCount = await query.CountAsync();

        query = (Sort, Sort) switch
        {
            ("pnl", _) => query.OrderByDescending(t => t.NetPnLAmount),
            ("date", _) => query.OrderByDescending(t => t.ClosedAtUtc),
            ("symbol", _) => query.OrderBy(t => t.Symbol),
            ("strategy", _) => query.OrderBy(t => t.StrategyId),
            ("opened", _) => query.OrderByDescending(t => t.OpenedAtUtc),
            _ => query.OrderByDescending(t => t.ClosedAtUtc),
        };

        TotalPages = (int)Math.Ceiling((double)TotalCount / PageSize);

        Trades = await query
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        AllSymbols = await db.Trades.Select(t => t.Symbol).Distinct().OrderBy(s => s).ToArrayAsync();
        AllStrategyIds = await db.Trades.Select(t => t.StrategyId).Distinct().OrderBy(s => s).ToArrayAsync();
        AllRunIds = await db.Trades.Where(t => t.RunId != null).Select(t => t.RunId!).Distinct().OrderByDescending(s => s).Take(50).ToArrayAsync();
    }
}

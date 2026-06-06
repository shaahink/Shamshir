namespace TradingEngine.Web.Pages.Trades;

public sealed class TradesIndexModel : PageModel
{
    public IReadOnlyList<TradeResult> Trades { get; private set; } = [];
    public int TotalPages { get; private set; }
    public int CurrentPage { get; private set; }

    public void OnGet(int page = 1)
    {
        CurrentPage = page;
        Trades = [];
        TotalPages = 1;
    }
}

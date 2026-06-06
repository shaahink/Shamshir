namespace TradingEngine.Web.Pages.Trades;

public sealed class TradeDetailModel : PageModel
{
    public TradeResult? Trade { get; private set; }

    public void OnGet(Guid id)
    {
        Trade = null;
    }
}

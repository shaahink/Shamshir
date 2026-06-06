namespace TradingEngine.Web.Pages;

public sealed class EventsModel : PageModel
{
    public IReadOnlyList<EngineEvent> Events { get; private set; } = [];

    public void OnGet()
    {
        Events = [];
    }
}

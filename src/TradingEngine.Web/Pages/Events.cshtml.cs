namespace TradingEngine.Web.Pages;

public sealed class EventsModel(ReportingDbContext db) : PageModel
{
    public IReadOnlyList<EngineEventEntity> Events { get; private set; } = [];

    public async Task OnGet()
    {
        Events = await db.Events.OrderByDescending(e => e.OccurredAtUtc).Take(100).ToListAsync();
    }
}

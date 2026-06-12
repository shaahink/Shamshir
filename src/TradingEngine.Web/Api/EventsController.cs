namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/events")]
public sealed class EventsController : ControllerBase
{
    private readonly IPipelineEventRepository _repo;

    public EventsController(IPipelineEventRepository repo)
    {
        _repo = repo;
    }

    [HttpGet]
    public async Task<IActionResult> GetEvents([FromQuery] string? runId, [FromQuery] int tail = 100, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(runId))
            return Ok(Array.Empty<object>());

        var events = await _repo.GetByRunIdAsync(runId, ct);
        return Ok(events.TakeLast(tail));
    }
}

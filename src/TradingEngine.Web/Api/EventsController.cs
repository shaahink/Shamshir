namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/events")]
public sealed class EventsController : ControllerBase
{
    [HttpGet]
    public IActionResult GetEvents([FromQuery] int tail = 100)
    {
        return Ok(Array.Empty<object>());
    }
}

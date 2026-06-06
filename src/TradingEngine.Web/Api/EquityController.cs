namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/equity")]
public sealed class EquityController : ControllerBase
{
    [HttpGet]
    public IActionResult GetEquity([FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        return Ok(Array.Empty<object>());
    }
}

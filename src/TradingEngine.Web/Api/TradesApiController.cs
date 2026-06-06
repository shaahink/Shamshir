namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/trades")]
public sealed class TradesApiController : ControllerBase
{
    [HttpGet]
    public IActionResult GetTrades(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string? strategyId)
    {
        return Ok(Array.Empty<object>());
    }

    [HttpGet("{id:guid}")]
    public IActionResult GetTradeDetail(Guid id)
    {
        return Ok(new { id, message = "Trade detail not yet implemented" });
    }
}

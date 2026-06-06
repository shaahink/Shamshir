namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/performance")]
public sealed class PerformanceApiController : ControllerBase
{
    [HttpGet]
    public IActionResult GetPerformance(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string? strategyId)
    {
        return Ok(new
        {
            TotalTrades = 0,
            Wins = 0,
            WinRate = 0.0,
            TotalNetPnL = 0.0,
            AvgHoldHours = 0.0
        });
    }
}

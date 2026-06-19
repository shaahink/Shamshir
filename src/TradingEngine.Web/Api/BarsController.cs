using TradingEngine.Web.Services;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/bars")]
public class BarsController : ControllerBase
{
    private readonly IBarQueryService _bars;

    public BarsController(IBarQueryService bars) => _bars = bars;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string symbol, [FromQuery] string timeframe = "H1",
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null, CancellationToken ct = default)
    {
        var bars = await _bars.GetBarsAsync(symbol, timeframe, from, to, ct);
        return Ok(bars);
    }
}

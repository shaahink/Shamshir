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
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null, [FromQuery] int limit = 5000,
        CancellationToken ct = default)
    {
        var bars = await _bars.GetBarsAsync(symbol, timeframe, from, to, ct);
        // iter-38 W-C3: cap the response so a wide date range can't return an unbounded payload; keep the
        // most recent bars in the window (charts want the latest data).
        var capped = Math.Clamp(limit, 1, 50_000);
        return Ok(bars.Count > capped ? bars.TakeLast(capped) : bars);
    }
}

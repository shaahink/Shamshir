namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/equity")]
public sealed class EquityController : ControllerBase
{
    private readonly IBacktestQueryService _query;

    public EquityController(IBacktestQueryService query)
    {
        _query = query;
    }

    [HttpGet]
    public async Task<IActionResult> GetEquity([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var points = await _query.GetEquityAsync(from, to, ct);
        return Ok(points.Select(p => new { p.TimestampUtc, p.Equity, p.Balance }));
    }
}

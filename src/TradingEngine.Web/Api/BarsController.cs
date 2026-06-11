namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/bars")]
public class BarsController : ControllerBase
{
    private readonly TradingDbContext _db;

    public BarsController(TradingDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string symbol, [FromQuery] string timeframe = "H1",
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var query = _db.Bars.AsQueryable();
        if (!string.IsNullOrEmpty(symbol))
            query = query.Where(b => b.Symbol == symbol);
        if (!string.IsNullOrEmpty(timeframe))
            query = query.Where(b => b.Timeframe == timeframe);
        if (from.HasValue)
            query = query.Where(b => b.OpenTimeUtc >= from.Value);
        if (to.HasValue)
            query = query.Where(b => b.OpenTimeUtc <= to.Value);

        var bars = await query.OrderBy(b => b.OpenTimeUtc)
            .Select(b => new { time = new DateTimeOffset(b.OpenTimeUtc).ToUnixTimeSeconds(), open = b.Open, high = b.High, low = b.Low, close = b.Close })
            .ToListAsync();
        return Ok(bars);
    }
}

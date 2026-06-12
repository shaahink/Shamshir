namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/protection")]
public sealed class ProtectionController : ControllerBase
{
    private readonly TradingDbContext _db;

    public ProtectionController(TradingDbContext db)
    {
        _db = db;
    }

    [HttpGet("days")]
    public async Task<IActionResult> GetDays([FromQuery] string? runId, CancellationToken ct)
    {
        IQueryable<DailyProtectionLedgerEntity> query = _db.DailyProtectionLedgers;
        if (!string.IsNullOrEmpty(runId))
            query = query.Where(d => d.RunId == runId);

        var days = await query
            .OrderBy(d => d.Date)
            .Select(d => new
            {
                d.Id,
                d.Date,
                d.StartEquity,
                d.MinEquity,
                d.EndEquity,
                d.MaxDailyDdUsedFraction,
                d.FinalGovernorState,
                d.BreachOccurred,
                d.TradesOpened,
                d.TradesClosed,
                d.SignalsBlocked,
            })
            .ToListAsync(ct);

        return Ok(days);
    }

    [HttpGet("days/{date}")]
    public async Task<IActionResult> GetDayDetails(DateTime date, [FromQuery] string? runId, CancellationToken ct)
    {
        IQueryable<DailyProtectionLedgerEntity> query = _db.DailyProtectionLedgers;
        if (!string.IsNullOrEmpty(runId))
            query = query.Where(d => d.RunId == runId);

        var day = await query
            .Where(d => d.Date.Date == date.Date)
            .SelectMany(d => _db.ProtectionLedgerEntries
                .Where(e => e.LedgerId == d.Id)
                .OrderBy(e => e.AtUtc)
                .Select(e => new
                {
                    e.AtUtc,
                    e.Category,
                    e.Reason,
                    e.EquityAtTime,
                    e.DailyDdUsedFraction,
                }))
            .ToListAsync(ct);

        if (day.Count == 0)
            return NotFound(new { error = $"No ledger entries for {date:yyyy-MM-dd}" });

        return Ok(day);
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/ctrader/sessions")]
public class VenueSessionsController(TradingDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetRecent([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        var sessions = await db.VenueSessions
            .OrderByDescending(s => s.OccurredAtUtc)
            .Take(limit)
            .Select(s => new
            {
                s.Id, s.RunId, s.Venue, s.Event, s.Detail, s.OccurredAtUtc
            })
            .ToListAsync(ct);
        return Ok(new { sessions });
    }

    [HttpGet("by-run/{runId}")]
    public async Task<IActionResult> GetByRun(string runId, CancellationToken ct)
    {
        var sessions = await db.VenueSessions
            .Where(s => s.RunId == runId)
            .OrderBy(s => s.OccurredAtUtc)
            .Select(s => new
            {
                s.Id, s.RunId, s.Venue, s.Event, s.Detail, s.OccurredAtUtc
            })
            .ToListAsync(ct);
        return Ok(new { sessions });
    }
}

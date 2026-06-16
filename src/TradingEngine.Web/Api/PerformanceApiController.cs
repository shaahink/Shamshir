using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence.Reporting;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/performance")]
public sealed class PerformanceApiController : ControllerBase
{
    private readonly ReportingDbContext _db;

    public PerformanceApiController(ReportingDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetPerformance(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] string? strategyId)
    {
        var query = _db.Trades.AsQueryable();
        if (from.HasValue) query = query.Where(t => t.ClosedAtUtc >= from.Value);
        if (to.HasValue) query = query.Where(t => t.ClosedAtUtc <= to.Value);
        if (!string.IsNullOrEmpty(strategyId)) query = query.Where(t => t.StrategyId == strategyId);

        var trades = await query
            .Select(t => new { t.NetPnLAmount, t.OpenedAtUtc, t.ClosedAtUtc })
            .ToListAsync(HttpContext.RequestAborted);

        if (trades.Count == 0)
        {
            return Ok(new { TotalTrades = 0, Wins = 0, WinRate = 0.0, TotalNetPnL = 0.0, AvgHoldHours = 0.0 });
        }

        var wins = trades.Count(t => t.NetPnLAmount > 0);
        var avgHoldHours = trades
            .Where(t => t.ClosedAtUtc > t.OpenedAtUtc)
            .Select(t => (t.ClosedAtUtc - t.OpenedAtUtc).TotalHours)
            .DefaultIfEmpty(0)
            .Average();

        return Ok(new
        {
            TotalTrades = trades.Count,
            Wins = wins,
            WinRate = (double)wins / trades.Count, // fraction (0..1) — matches other endpoints
            TotalNetPnL = (double)trades.Sum(t => t.NetPnLAmount),
            AvgHoldHours = avgHoldHours,
        });
    }
}

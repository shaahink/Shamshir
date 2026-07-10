using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/scoreboard")]
public sealed class ScoreboardController : ControllerBase
{
    private readonly TradingDbContext _db;
    private readonly ILogger<ScoreboardController> _logger;

    public ScoreboardController(TradingDbContext db, ILogger<ScoreboardController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var parks = await _db.Set<StrategyCellParkEntity>().AsNoTracking().ToListAsync(ct);
        var configs = await _db.StrategyConfigs.AsNoTracking().ToListAsync(ct);
        var runs = await _db.BacktestRuns.AsNoTracking()
            .Where(r => r.CompletedAtUtc != default)
            .OrderByDescending(r => r.CompletedAtUtc)
            .Take(500)
            .ToListAsync(ct);

        var trades = await _db.Trades.AsNoTracking()
            .Where(t => runs.Select(r => r.RunId).Contains(t.RunId))
            .ToListAsync(ct);

        var cells = new List<object>();
        var seen = new HashSet<string>();

        foreach (var run in runs)
        {
            var runPlan = ParseRunPlan(run.RunPlanJson);
            foreach (var row in runPlan)
            {
                var key = $"{row.StrategyId}|{row.Symbol}|{row.Timeframe}";
                if (!seen.Add(key)) continue;

                var cellTrades = trades.Where(t => t.RunId == run.RunId
                    && t.StrategyId == row.StrategyId
                    && t.Symbol == row.Symbol
                    && t.EntryTimeframe == row.Timeframe).ToList();
                var avgR = cellTrades.Count > 0 ? cellTrades.Average(t => t.RMultiple) : 0.0;
                var tradesPerWeek = 0.0;
                if (run.BacktestFrom != default && run.BacktestTo != default)
                {
                    var weeks = (run.BacktestTo - run.BacktestFrom).TotalDays / 7.0;
                    if (weeks > 0) tradesPerWeek = cellTrades.Count / weeks;
                }

                var park = parks.FirstOrDefault(p => p.StrategyId == row.StrategyId && p.Symbol == row.Symbol && p.Timeframe == row.Timeframe);
                var config = configs.FirstOrDefault(c => c.Id == row.StrategyId);

                cells.Add(new
                {
                    strategyId = row.StrategyId,
                    strategyName = config?.DisplayName ?? row.StrategyId,
                    symbol = row.Symbol,
                    timeframe = row.Timeframe,
                    enabled = config?.Enabled ?? true,
                    parked = park is not null,
                    parkReason = park?.Reason,
                    latestAvgR = Math.Round(avgR, 3),
                    totalTrades = cellTrades.Count,
                    tradesPerWeek = Math.Round(tradesPerWeek, 1),
                    lastRunId = run.RunId,
                    lastRunAt = run.CompletedAtUtc.ToString("O"),
                    thesis = config?.Thesis ?? "",
                });
            }
        }

        return Ok(cells.OrderByDescending(c => ((dynamic)c).latestAvgR));
    }

    [HttpPost("{strategyId}/park")]
    public async Task<IActionResult> Park(string strategyId, [FromBody] ParkRequest req, CancellationToken ct)
    {
        var existing = await _db.Set<StrategyCellParkEntity>()
            .FirstOrDefaultAsync(e => e.StrategyId == strategyId && e.Symbol == req.Symbol && e.Timeframe == req.Timeframe, ct);

        if (existing is not null)
        {
            existing.Reason = req.Reason ?? "";
            existing.ParkedAtUtc = DateTime.UtcNow;
        }
        else
        {
            _db.Set<StrategyCellParkEntity>().Add(new StrategyCellParkEntity
            {
                Id = Guid.NewGuid(),
                StrategyId = strategyId,
                Symbol = req.Symbol,
                Timeframe = req.Timeframe,
                Reason = req.Reason ?? "",
                ParkedAtUtc = DateTime.UtcNow,
            });
        }

        await _db.SaveChangesAsync(ct);
        return Ok(new { parked = true });
    }

    [HttpPost("{strategyId}/unpark")]
    public async Task<IActionResult> Unpark(string strategyId, [FromBody] ParkRequest req, CancellationToken ct)
    {
        var existing = await _db.Set<StrategyCellParkEntity>()
            .Where(e => e.StrategyId == strategyId && e.Symbol == req.Symbol && e.Timeframe == req.Timeframe)
            .ToListAsync(ct);

        if (existing.Count > 0)
        {
            _db.Set<StrategyCellParkEntity>().RemoveRange(existing);
            await _db.SaveChangesAsync(ct);
        }

        return Ok(new { unparked = true });
    }

    private List<RunPlanRow> ParseRunPlan(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try
        {
            var entries = System.Text.Json.JsonSerializer.Deserialize<List<RunPlanRow>>(json);
            return entries ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse RunPlanJson — skipping run from scoreboard");
            return [];
        }
    }

    private sealed record RunPlanRow(string StrategyId = "", string Symbol = "", string Timeframe = "");
}

public sealed record ParkRequest
{
    public required string Symbol { get; init; }
    public required string Timeframe { get; init; }
    public string? Reason { get; init; }
}

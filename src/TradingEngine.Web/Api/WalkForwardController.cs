using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain.Experiments;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Web.Hubs;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/walk-forward")]
public sealed class WalkForwardController : ControllerBase
{
    private readonly TradingDbContext _db;
    private readonly WalkForwardBackgroundService _wf;
    private readonly IHubContext<WalkForwardHub> _hub;

    public WalkForwardController(TradingDbContext db, WalkForwardBackgroundService wf, IHubContext<WalkForwardHub> hub)
    {
        _db = db;
        _wf = wf;
        _hub = hub;
    }

    [HttpPost("start")]
    public IActionResult Start([FromBody] WalkForwardRequest req)
    {
        if (req.Strategies is not { Length: > 0 })
            return BadRequest(new { error = "At least one strategy is required." });
        if (req.Symbols is not { Length: > 0 })
            return BadRequest(new { error = "At least one symbol is required." });
        if (req.Timeframes is not { Length: > 0 })
            return BadRequest(new { error = "At least one timeframe is required." });
        if (req.ParamGrid is not { Count: > 0 })
            return BadRequest(new { error = "At least one parameter with values is required." });

        var spec = new WalkForwardSpec(req.Folds, req.TrainFraction)
        {
            Strategies = req.Strategies,
            Symbols = req.Symbols,
            Timeframes = req.Timeframes,
            From = req.From,
            To = req.To,
            ParamGrid = req.ParamGrid,
            Balance = req.Balance,
        };

        var job = new WalkForwardJobEntity
        {
            Id = Guid.NewGuid(),
            SpecJson = JsonSerializer.Serialize(spec),
            Status = "pending",
            CreatedAtUtc = DateTime.UtcNow,
        };

        _db.Set<WalkForwardJobEntity>().Add(job);
        _db.SaveChanges();

        _wf.Enqueue(job);

        return Ok(new { jobId = job.Id, status = "enqueued" });
    }

    [HttpGet("jobs")]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var jobs = await _db.Set<WalkForwardJobEntity>()
            .OrderByDescending(j => j.CreatedAtUtc)
            .Take(50)
            .Select(j => new { j.Id, j.Status, j.TotalWindows, j.CompletedWindows, j.CreatedAtUtc, j.CompletedAtUtc, j.ErrorMessage })
            .ToListAsync(ct);
        return Ok(jobs);
    }

    [HttpGet("jobs/{jobId:guid}")]
    public async Task<IActionResult> GetJob(Guid jobId, CancellationToken ct)
    {
        var job = await _db.Set<WalkForwardJobEntity>()
            .Include(j => j.Windows)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null) return NotFound();

        return Ok(new
        {
            job.Id, job.Status, job.TotalWindows, job.CompletedWindows, job.CreatedAtUtc, job.CompletedAtUtc, job.ErrorMessage,
            windows = job.Windows.OrderBy(w => w.WindowIndex).Select(w => new
            {
                w.WindowIndex,
                trainFrom = w.TrainFromUtc.ToString("O"),
                trainTo = w.TrainToUtc.ToString("O"),
                testFrom = w.TestFromUtc.ToString("O"),
                testTo = w.TestToUtc.ToString("O"),
                w.StrategyId, w.Symbol, w.Timeframe,
                w.ChosenParamsJson,
                w.TestRunId,
                w.TestNetProfit,
                w.TestTotalTrades,
                testWinRatePct = w.TestWinRatePct,
                w.TrialsCount,
            }),
        });
    }

    [HttpGet("jobs/{jobId:guid}/equity")]
    public IActionResult GetEquity(Guid jobId)
    {
        return Ok(new { points = Array.Empty<object>() });
    }
}

public sealed record WalkForwardRequest
{
    public int Folds { get; init; } = 4;
    public double TrainFraction { get; init; } = 0.7;
    public required string[] Strategies { get; init; }
    public required string[] Symbols { get; init; }
    public required string[] Timeframes { get; init; }
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }
    public required Dictionary<string, decimal[]> ParamGrid { get; init; }
    public decimal Balance { get; init; } = 100_000m;
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Web.Api;

/// <summary>
/// P3.2 (Q6) — the persistence + review surface for research pipelines. The <c>research pipeline</c> CLI
/// verbs drive these endpoints so ALL pipeline state lives in one place (the running app's DB, F10), the
/// UI (<c>/research</c>, P3.3) sees it live, and a pipeline is resumable by id. The executor itself is a
/// dumb sequential runner in the CLI (PLAN §12) — this controller only stores and serves state and lets
/// the owner approve/reject a parked <c>owner-gate</c> step.
/// </summary>
[ApiController]
[Route("api/research/pipelines")]
public sealed class ResearchPipelinesController : ControllerBase
{
    private readonly TradingDbContext _db;
    private readonly ILogger<ResearchPipelinesController> _logger;

    public ResearchPipelinesController(TradingDbContext db, ILogger<ResearchPipelinesController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var rows = await _db.ResearchPipelines
            .AsNoTracking()
            .OrderByDescending(p => p.StartedAtUtc)
            .Take(100)
            .Select(p => new
            {
                p.Id, p.Name, p.Status, p.CurrentStepIndex,
                startedAtUtc = p.StartedAtUtc, completedAtUtc = p.CompletedAtUtc,
                stepCount = p.Steps.Count,
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var p = await _db.ResearchPipelines
            .AsNoTracking()
            .Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound(new { error = $"Pipeline '{id}' not found." });
        return Ok(MapDetail(p));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePipelineRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { error = "Name is required." });
        if (req.Steps is not { Count: > 0 })
            return BadRequest(new { error = "At least one step is required." });

        var id = Guid.NewGuid();
        var entity = new ResearchPipelineEntity
        {
            Id = id,
            Name = req.Name.Trim(),
            PlaybookJson = string.IsNullOrWhiteSpace(req.PlaybookJson) ? "{}" : req.PlaybookJson,
            Status = "running",
            CurrentStepIndex = 0,
            StartedAtUtc = DateTime.UtcNow,
            ArtifactDir = req.ArtifactDir,
            Steps = [.. req.Steps.Select((s, i) => new ResearchPipelineStepEntity
            {
                Id = Guid.NewGuid(),
                PipelineId = id,
                StepIndex = i,
                Kind = s.Kind ?? "",
                Status = "pending",
                ParamHash = s.ParamHash ?? "",
            })],
        };

        _db.ResearchPipelines.Add(entity);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Research pipeline created. Id={PipelineId} Name={Name} Steps={Steps}",
            id, entity.Name, entity.Steps.Count);
        return Ok(MapDetail(entity));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePipelineRequest req, CancellationToken ct)
    {
        var p = await _db.ResearchPipelines.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound(new { error = $"Pipeline '{id}' not found." });

        if (!string.IsNullOrWhiteSpace(req.Status)) p.Status = req.Status;
        if (req.CurrentStepIndex is { } idx) p.CurrentStepIndex = idx;
        if (req.ArtifactDir is not null) p.ArtifactDir = req.ArtifactDir;
        if (req.Completed == true) p.CompletedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(MapDetail(await LoadAsync(id, ct)));
    }

    [HttpPut("{id:guid}/steps/{index:int}")]
    public async Task<IActionResult> UpdateStep(Guid id, int index, [FromBody] UpdateStepRequest req, CancellationToken ct)
    {
        var step = await _db.ResearchPipelineSteps
            .FirstOrDefaultAsync(s => s.PipelineId == id && s.StepIndex == index, ct);
        if (step is null) return NotFound(new { error = $"Step {index} of pipeline '{id}' not found." });

        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            if (string.Equals(req.Status, "running", StringComparison.Ordinal) && step.StartedAtUtc is null)
                step.StartedAtUtc = DateTime.UtcNow;
            if (IsTerminalStep(req.Status))
                step.CompletedAtUtc = DateTime.UtcNow;
            step.Status = req.Status;
        }
        if (req.VerdictJson is not null) step.VerdictJson = req.VerdictJson;
        if (req.ArtifactPath is not null) step.ArtifactPath = req.ArtifactPath;
        if (!string.IsNullOrWhiteSpace(req.ParamHash)) step.ParamHash = req.ParamHash;

        await _db.SaveChangesAsync(ct);
        return Ok(MapStep(step));
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct) => await ResolveGateAsync(id, approve: true, ct);

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct) => await ResolveGateAsync(id, approve: false, ct);

    private async Task<IActionResult> ResolveGateAsync(Guid id, bool approve, CancellationToken ct)
    {
        var p = await _db.ResearchPipelines.Include(x => x.Steps).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null) return NotFound(new { error = $"Pipeline '{id}' not found." });

        var gate = p.Steps.FirstOrDefault(s => s.Status == "awaiting-owner");
        if (gate is null)
            return BadRequest(new { error = "No owner-gate is awaiting approval on this pipeline." });

        gate.Status = approve ? "approved" : "rejected";
        gate.CompletedAtUtc = DateTime.UtcNow;
        // Approving releases the pipeline back to the CLI executor to resume; rejecting terminates it.
        p.Status = approve ? "running" : "cancelled";
        if (!approve) p.CompletedAtUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Owner {Decision} gate step {Step} on pipeline {PipelineId}.",
            approve ? "approved" : "rejected", gate.StepIndex, id);
        return Ok(MapDetail(await LoadAsync(id, ct)));
    }

    private async Task<ResearchPipelineEntity> LoadAsync(Guid id, CancellationToken ct) =>
        await _db.ResearchPipelines.AsNoTracking().Include(x => x.Steps).FirstAsync(x => x.Id == id, ct);

    private static bool IsTerminalStep(string status) =>
        status is "passed" or "failed" or "approved" or "rejected" or "skipped";

    private static object MapDetail(ResearchPipelineEntity p) => new
    {
        p.Id, p.Name, p.Status, p.CurrentStepIndex, p.PlaybookJson, p.ArtifactDir,
        startedAtUtc = p.StartedAtUtc, completedAtUtc = p.CompletedAtUtc,
        steps = p.Steps.OrderBy(s => s.StepIndex).Select(MapStep),
    };

    private static object MapStep(ResearchPipelineStepEntity s) => new
    {
        s.StepIndex, s.Kind, s.Status, s.ParamHash, s.VerdictJson, s.ArtifactPath,
        startedAtUtc = s.StartedAtUtc, completedAtUtc = s.CompletedAtUtc,
    };
}

public sealed record CreatePipelineRequest
{
    public string Name { get; init; } = "";
    public string? PlaybookJson { get; init; }
    public string? ArtifactDir { get; init; }
    public List<CreateStepRequest>? Steps { get; init; }
}

public sealed record CreateStepRequest
{
    public string? Kind { get; init; }
    public string? ParamHash { get; init; }
}

public sealed record UpdatePipelineRequest
{
    public string? Status { get; init; }
    public int? CurrentStepIndex { get; init; }
    public string? ArtifactDir { get; init; }
    public bool? Completed { get; init; }
}

public sealed record UpdateStepRequest
{
    public string? Status { get; init; }
    public string? VerdictJson { get; init; }
    public string? ArtifactPath { get; init; }
    public string? ParamHash { get; init; }
}

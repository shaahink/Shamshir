namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/experiments")]
public sealed class ExperimentsController : ControllerBase
{
    private readonly ExperimentRunner _runner;
    private readonly IExperimentRepository _repo;
    private readonly ILogger<ExperimentsController> _logger;

    public ExperimentsController(
        ExperimentRunner runner,
        IExperimentRepository repo,
        ILogger<ExperimentsController> logger)
    {
        _runner = runner;
        _repo = repo;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ExperimentSpec spec, CancellationToken ct)
    {
        try
        {
            var result = await _runner.RunAsync(spec, ct);
            if (result.Success)
                return Ok(new { experimentId = result.ExperimentId, status = "completed", result.VariantScores });
            return BadRequest(new { error = result.ErrorMessage });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create experiment");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var experiments = await _repo.GetAllAsync(ct);
        var list = experiments.Select(e => new
        {
            e.Id,
            e.Name,
            e.Status,
            e.CreatedUtc,
            e.CompletedUtc,
            RunCount = e.Runs.Count,
        });
        return Ok(list);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var experiment = await _repo.GetByIdAsync(id, ct);
        if (experiment is null)
            return NotFound(new { error = $"Experiment {id} not found" });

        return Ok(new
        {
            experiment.Id,
            experiment.Name,
            experiment.Hypothesis,
            experiment.SpecJson,
            experiment.Status,
            experiment.CreatedUtc,
            experiment.CompletedUtc,
            Runs = experiment.Runs.Select(r => new
            {
                r.Id,
                r.VariantLabel,
                r.FoldIndex,
                r.FoldRole,
                r.BacktestRunId,
                r.ScoreJson,
            }),
        });
    }

    [HttpGet("{id:guid}/report")]
    public async Task<IActionResult> GetReport(Guid id, CancellationToken ct)
    {
        // iter-38 W-B2: resolve the report for THIS experiment instead of returning the first REPORT.md
        // found. ExperimentReportWriter writes to docs/experiments/{Name}-{shortId}/REPORT.md where
        // shortId = id.ToString("N")[..8]; mirror that naming exactly.
        var experiment = await _repo.GetByIdAsync(id, ct);
        if (experiment is null)
            return NotFound(new { error = $"Experiment {id} not found" });

        var shortId = id.ToString("N")[..8];
        var baseDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "experiments");
        var reportPath = Path.Combine(baseDir, $"{experiment.Name}-{shortId}", "REPORT.md");

        if (!System.IO.File.Exists(reportPath))
            return NotFound(new { error = $"Report not found for experiment {id}" });

        var content = await System.IO.File.ReadAllTextAsync(reportPath, ct);
        return Content(content, "text/markdown");
    }
}

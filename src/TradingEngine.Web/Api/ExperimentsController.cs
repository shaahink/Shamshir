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
    public IActionResult GetReport(Guid id)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "experiments");
        foreach (var subdir in Directory.GetDirectories(dir))
        {
            var reportPath = Path.Combine(subdir, "REPORT.md");
            if (System.IO.File.Exists(reportPath))
            {
                var content = System.IO.File.ReadAllText(reportPath);
                return Content(content, "text/markdown");
            }
        }

        return NotFound(new { error = $"Report not found for experiment {id}" });
    }
}

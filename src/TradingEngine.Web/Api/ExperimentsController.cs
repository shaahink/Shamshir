namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/experiments")]
public sealed class ExperimentsController : ControllerBase
{
    private readonly ExperimentRunner _runner;
    private readonly IExperimentRepository _repo;
    private readonly SetupScoreService _scorer;
    private readonly SplitHalfPersistenceService _persistence;
    private readonly ILogger<ExperimentsController> _logger;

    public ExperimentsController(
        ExperimentRunner runner,
        IExperimentRepository repo,
        SetupScoreService scorer,
        SplitHalfPersistenceService persistence,
        ILogger<ExperimentsController> logger)
    {
        _runner = runner;
        _repo = repo;
        _scorer = scorer;
        _persistence = persistence;
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

    // R0.2: score a run via SetupScore v1
    [HttpPost("score")]
    public async Task<IActionResult> Score([FromBody] ScoreRequest req, CancellationToken ct)
    {
        var result = await _scorer.ScoreRunAsync(
            req.BacktestRunId,
            req.ExperimentId,
            req.VariantLabel,
            req.FoldIndex,
            req.FoldRole,
            ct,
            req.StrategyId,
            req.WalkForwardJobId);

        if (result.Passed)
        {
            return Ok(new
            {
                verdict = "PASS",
                score = result.Composite,
                version = result.Version,
                scoreJson = result.ScoreJson,
            });
        }

        return Ok(new
        {
            verdict = "FAIL",
            reason = result.Reason,
            score = (double?)null,
            version = result.Version,
        });
    }

    // iter-structural-edge S0: the F64 split-half selection test, parameterized (PLAN §5 — the
    // owner's 5-minute verification). `experiment` accepts a GUID prefix (e.g. 075D5240).
    [HttpGet("persistence")]
    public async Task<IActionResult> Persistence(
        [FromQuery] string experiment, [FromQuery] string split, [FromQuery] double @base = 100_000,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(experiment))
        {
            return BadRequest(new { error = "experiment (id or prefix) is required" });
        }

        if (!DateOnly.TryParse(split, System.Globalization.CultureInfo.InvariantCulture, out var splitDate))
        {
            return BadRequest(new { error = $"split '{split}' is not a date (yyyy-MM-dd)" });
        }

        var report = await _persistence.ComputeAsync(experiment, splitDate, @base, ct);
        if (report.Error is not null)
        {
            return NotFound(new { error = report.Error });
        }

        return Ok(report);
    }

    // R0.2: scoreboard — top N experiment runs
    [HttpGet("{id:guid}/scoreboard")]
    public async Task<IActionResult> Scoreboard(Guid id, [FromQuery] int top = 20, CancellationToken ct = default)
    {
        var result = await _scorer.GetScoreboardAsync(id, top, ct);
        if (result.Error is not null)
            return NotFound(new { error = result.Error });

        return Ok(new
        {
            experimentId = result.ExperimentId,
            experimentName = result.ExperimentName,
            totalRuns = result.TotalRuns,
            scoredRuns = result.ScoredRuns,
            nullRuns = result.NullRuns,
            top = result.Top.Select(e => new
            {
                e.BacktestRunId,
                e.VariantLabel,
                composite = e.Score!.Composite,
                version = e.Score.VersionKind,
                expectancy = e.Score.Components.Expectancy,
                drawdownPct = e.Score.Components.DrawdownPct,
                consistency = e.Score.Components.Consistency,
                trades = e.Score.Trades,
            }),
        });
    }
}

public sealed record ScoreRequest
{
    public string BacktestRunId { get; init; } = "";
    public Guid? ExperimentId { get; init; }
    public string? VariantLabel { get; init; }
    public int? FoldIndex { get; init; }
    public string? FoldRole { get; init; }
    public string? StrategyId { get; init; }

    // R3.2 (F62): when set, ScoreRunAsync computes a real OOS ratio from this walk-forward job's
    // WalkForwardWindowResultEntity rows (Sum(TestNetProfit) / Sum(PlateauValue) across folds)
    // instead of leaving RobustnessOos/OosRatio null — this is what upgrades a cell from
    // sv1-partial to full sv1.
    public Guid? WalkForwardJobId { get; init; }
}

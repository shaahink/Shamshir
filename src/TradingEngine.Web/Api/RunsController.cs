using TradingEngine.CTraderRunner;
using TradingEngine.Web.Dtos.Runs;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/runs")]
public sealed class RunsController : ControllerBase
{
    private readonly IRunQueryService _query;
    private readonly IBacktestCommandService _command;
    private readonly IBacktestQueryService _legacyQuery;
    private readonly IJournalQueryRepository _journals;
    private readonly IBacktestRunRepository _runRepo;
    private readonly BacktestOrchestrator _orchestrator;
    private readonly ILogger<RunsController> _logger;

    public RunsController(
        IRunQueryService query,
        IBacktestCommandService command,
        IBacktestQueryService legacyQuery,
        IJournalQueryRepository journals,
        IBacktestRunRepository runRepo,
        BacktestOrchestrator orchestrator,
        ILogger<RunsController> logger)
    {
        _query = query;
        _command = command;
        _legacyQuery = legacyQuery;
        _journals = journals;
        _runRepo = runRepo;
        _orchestrator = orchestrator;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var runs = await _query.GetRunsAsync(ct);
        return Ok(runs);
    }

    [HttpGet("{runId}")]
    public async Task<IActionResult> Get(string runId, CancellationToken ct)
    {
        var run = await _query.GetRunAsync(runId, ct);
        if (run is null) return NotFound(new { error = $"Run {runId} not found" });
        return Ok(run);
    }

    [HttpPost]
    public async Task<IActionResult> Start([FromBody] StartRunRequest req, CancellationToken ct)
    {
        var symList = req.Symbols is { Count: > 0 }
            ? req.Symbols.Select(s => s.ToUpperInvariant()).ToArray()
            : new[] { req.Symbol.ToUpperInvariant() };

        var perList = req.Periods is { Count: > 0 }
            ? req.Periods.Select(p => p.ToUpperInvariant()).ToArray()
            : new[] { req.Period.ToUpperInvariant() };

        var stratList = req.StrategyIds is { Count: > 0 }
            ? req.StrategyIds.Select(s => s.Trim()).ToArray()
            : Array.Empty<string>();

        var cfg = new BacktestConfig
        {
            Symbol = req.Symbol.ToUpperInvariant(),
            Period = req.Period.ToLowerInvariant(),
            Start = req.Start,
            End = req.End,
            Balance = req.Balance,
            CommissionPerMillion = req.CommissionPerMillion,
            SpreadPips = req.SpreadPips,
            Symbols = symList,
            Periods = perList,
        };

        if (stratList.Length > 0)
            cfg.CustomParams["StrategyIds"] = string.Join(",", stratList);
        if (!string.IsNullOrWhiteSpace(req.RiskProfileId))
            cfg.CustomParams["RiskProfileId"] = req.RiskProfileId.Trim();
        if (!string.IsNullOrWhiteSpace(req.Venue))
            cfg.CustomParams["Venue"] = req.Venue.Trim().ToLowerInvariant();
        if (req.StrategyOverrides is { Count: > 0 })
            cfg.CustomParams["StrategyOverrides"] = System.Text.Json.JsonSerializer.Serialize(req.StrategyOverrides);

        var runId = await _command.StartAsync(cfg, ct);
        var state = _orchestrator.GetState(runId);
        _logger.LogInformation("Run started. RunId={RunId}", runId);

        return Ok(new StartRunResponse { RunId = runId, Status = state?.Status ?? "started" });
    }

    [HttpDelete("{runId}")]
    public async Task<IActionResult> Cancel(string runId)
    {
        _orchestrator.Cancel(runId);
        return Ok(new { cancelled = true });
    }

    // iter-36 K6 / iter-37 F3 — "duplicate with changes": re-run over the SAME dataset window with an
    // optionally-changed strategy set / risk profile / overrides. New run keeps the source DatasetId, gets
    // a fresh ConfigSetId, and records ParentRunId = source (lineage). Omitting all fields = deterministic re-run.
    [HttpPost("{runId}/duplicate")]
    public async Task<IActionResult> Duplicate(string runId, [FromBody] DuplicateRunRequest? req, CancellationToken ct)
    {
        var source = await _runRepo.GetByIdAsync(runId, ct);
        if (source is null) return NotFound(new { error = $"Run {runId} not found" });

        req ??= new DuplicateRunRequest();
        var symbols = ParseJsonArray(source.Symbols);
        var periods = ParseJsonArray(source.Periods);

        var cfg = new BacktestConfig
        {
            Symbol = source.Symbol,
            Period = source.Period.ToLowerInvariant(),
            Start = source.BacktestFrom,
            End = source.BacktestTo,
            Balance = source.InitialBalance,
            Symbols = symbols.Length > 0 ? symbols : new[] { source.Symbol },
            Periods = periods.Length > 0 ? periods : new[] { source.Period },
        };

        if (req.StrategyIds is { Count: > 0 })
            cfg.CustomParams["StrategyIds"] = string.Join(",", req.StrategyIds.Select(s => s.Trim()));
        if (!string.IsNullOrWhiteSpace(req.RiskProfileId))
            cfg.CustomParams["RiskProfileId"] = req.RiskProfileId.Trim();
        if (!string.IsNullOrWhiteSpace(req.Venue))
            cfg.CustomParams["Venue"] = req.Venue.Trim().ToLowerInvariant();
        if (req.StrategyOverrides is { Count: > 0 })
            cfg.CustomParams["StrategyOverrides"] = System.Text.Json.JsonSerializer.Serialize(req.StrategyOverrides);
        cfg.CustomParams["ParentRunId"] = runId;

        var newRunId = await _command.StartAsync(cfg, ct);
        var state = _orchestrator.GetState(newRunId);
        _logger.LogInformation("Run {NewRunId} duplicated from {SourceRunId}", newRunId, runId);
        return Ok(new StartRunResponse { RunId = newRunId, Status = state?.Status ?? "started" });
    }

    private static string[] ParseJsonArray(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch { return []; }
    }

    [HttpGet("{runId}/trades")]
    public async Task<IActionResult> GetTrades(string runId, CancellationToken ct)
    {
        var trades = await _query.GetRunTradesAsync(runId, ct);
        return Ok(trades);
    }

    // iter-36 K5: the single journal is the lossless StepRecord stream (JournalEntries), SQL-paged by seq.
    // Repointed off the old PipelineEvents (_legacyQuery) onto IJournalQueryRepository — what iter-37's
    // unified journal view consumes (orderId/violations/costs/per-strategy verdicts all on the StepRecord).
    [HttpGet("{runId}/journal")]
    public async Task<IActionResult> GetJournal(
        string runId,
        [FromQuery] string? kind,
        [FromQuery] long? afterSeq,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var entries = await _journals.GetByRunAsync(runId, afterSeq, Math.Clamp(limit, 1, 1000), ct);
        if (!string.IsNullOrWhiteSpace(kind))
            entries = entries.Where(e => string.Equals(e.EventKind, kind, StringComparison.OrdinalIgnoreCase)).ToList();
        return Ok(entries);
    }

    // NDJSON export of the StepRecord journal (iter-37 F3 "Download journal"). One JSON object per line,
    // streamed in seq order so a large run doesn't buffer in memory.
    [HttpGet("{runId}/journal/export")]
    public async Task ExportJournal(string runId, [FromQuery] long? afterSeq, CancellationToken ct = default)
    {
        Response.ContentType = "application/x-ndjson";
        Response.Headers.TryAdd("Content-Disposition", $"attachment; filename=\"{runId}-journal.ndjson\"");
        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        await foreach (var entry in _journals.StreamByRunAsync(runId, afterSeq, ct))
        {
            await Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(entry, opts) + "\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    [HttpGet("{runId}/equity")]
    public async Task<IActionResult> GetEquity(string runId, CancellationToken ct)
    {
        var points = await _query.GetRunEquityAsync(runId, ct);
        return Ok(points);
    }

    [HttpGet("{runId}/daily-pnl")]
    public async Task<IActionResult> GetDailyPnL(string runId, CancellationToken ct)
    {
        var daily = await _query.GetRunDailyPnLAsync(runId, ct);
        return Ok(daily);
    }

    [HttpGet("{runId}/analytics")]
    public async Task<IActionResult> GetAnalytics(string runId, CancellationToken ct)
    {
        var analytics = await _query.GetRunAnalyticsAsync(runId, ct);
        if (analytics is null) return Ok(new RunAnalyticsResponse());
        return Ok(analytics);
    }

    [HttpGet("/api/equity")]
    public async Task<IActionResult> GetEquity([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var points = await _legacyQuery.GetEquityAsync(from, to, ct);
        return Ok(points.Select(p => new { p.TimestampUtc, p.Equity, p.Balance }));
    }
}

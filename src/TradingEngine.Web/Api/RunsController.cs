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
    private readonly RunNarrativeService? _narrative;
    private readonly IRunDataCache? _cache;
    private readonly ILogger<RunsController> _logger;

    public RunsController(
        IRunQueryService query,
        IBacktestCommandService command,
        IBacktestQueryService legacyQuery,
        IJournalQueryRepository journals,
        IBacktestRunRepository runRepo,
        BacktestOrchestrator orchestrator,
        ILogger<RunsController> logger,
        RunNarrativeService? narrative = null,
        IRunDataCache? cache = null)
    {
        _query = query;
        _command = command;
        _legacyQuery = legacyQuery;
        _journals = journals;
        _runRepo = runRepo;
        _orchestrator = orchestrator;
        _narrative = narrative;
        _cache = cache;
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
        // iter-strategy-system P1 (D3): the row-based builder sends explicit rows
        // (strategy × symbol × timeframe × pack). When present they supersede the legacy cross-product —
        // symbols/periods/strategies are DERIVED from the enabled rows.
        var rowPlan = req.Rows is { Count: > 0 } ? RunPlanBuilder.FromRows(req.Rows) : null;

        string[] validSymbols, validPeriods, stratList;
        if (rowPlan is { Entries.Count: > 0 })
        {
            validSymbols = rowPlan.Entries.Select(e => e.Symbol).Distinct().ToArray();
            validPeriods = rowPlan.Entries.Select(e => e.Timeframe).Distinct().ToArray();
            stratList = rowPlan.Entries.Select(e => e.StrategyId).Distinct().ToArray();
        }
        else
        {
            var symList = req.Symbols is { Count: > 0 }
                ? req.Symbols.Select(s => s.ToUpperInvariant()).ToArray()
                : new[] { "EURUSD" };
            var perList = req.Periods is { Count: > 0 }
                ? req.Periods.Select(p => p.ToUpperInvariant()).ToArray()
                : new[] { "H1" };
            stratList = req.StrategyIds is { Count: > 0 }
                ? req.StrategyIds.Select(s => s.Trim()).ToArray()
                : Array.Empty<string>();
            validSymbols = symList.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            validPeriods = perList.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        }

        var activeRuns = _orchestrator.GetAll()
            .Where(r => r.Status is "starting" or "running")
            .Select(r => r.RunId)
            .ToList();
        if (activeRuns.Count > 0)
        {
            return Conflict(new
            {
                error = "A backtest is already running. Wait for it to complete or cancel it first.",
                activeRunIds = activeRuns,
            });
        }

        var errors = new List<string>();
        if (req.Rows is { Count: > 0 } && rowPlan is not { Entries.Count: > 0 })
            errors.Add("At least one enabled row is required.");
        if (validSymbols.Length == 0) errors.Add("At least one symbol is required.");
        if (validPeriods.Length == 0) errors.Add("At least one timeframe is required.");
        if (req.Start >= req.End) errors.Add("Start date must be before end date.");
        if (req.Balance <= 0) errors.Add("Balance must be positive.");
        if (errors.Count > 0)
            return BadRequest(new { error = "Invalid backtest request.", details = errors });

        var cfg = new BacktestConfig
        {
            Symbol = validSymbols[0],
            Period = validPeriods[0].ToLowerInvariant(),
            Start = req.Start,
            End = req.End,
            Balance = req.Balance,
            CommissionPerMillion = req.CommissionPerMillion,
            SpreadPips = req.SpreadPips,
            Symbols = validSymbols,
            Periods = validPeriods,
        };

        if (stratList.Length > 0)
            cfg.CustomParams["StrategyIds"] = string.Join(",", stratList);
        if (rowPlan is { Entries.Count: > 0 })
            cfg.CustomParams["RunRows"] = System.Text.Json.JsonSerializer.Serialize(rowPlan.Entries);
        // Run-level governor toggle (D4) — always recorded so the persisted run shows the choice.
        cfg.CustomParams["GovernorEnabled"] = req.GovernorEnabled ? "true" : "false";
        // Run-level protection toggles (P5) — default "true" means ruleset defaults apply.
        cfg.CustomParams["DailyDdEnabled"] = req.DailyDdEnabled ? "true" : "false";
        cfg.CustomParams["MaxDdEnabled"] = req.MaxDdEnabled ? "true" : "false";
            cfg.CustomParams["ForceCloseOnBreachEnabled"] = req.ForceCloseOnBreachEnabled ? "true" : "false";
        if (!string.IsNullOrWhiteSpace(req.RiskProfileId))
            cfg.CustomParams["RiskProfileId"] = req.RiskProfileId.Trim();
        cfg.CustomParams["Venue"] = (!string.IsNullOrWhiteSpace(req.Venue) ? req.Venue!.Trim().ToLowerInvariant() : null) ?? "replay";
        if (req.StrategyOverrides is { Count: > 0 })
            cfg.CustomParams["StrategyOverrides"] = System.Text.Json.JsonSerializer.Serialize(req.StrategyOverrides);
        if (!string.IsNullOrWhiteSpace(req.UsePackId))
            cfg.CustomParams["UsePackId"] = req.UsePackId.Trim();
        if (req.PerStrategyPackIds is { Count: > 0 })
            cfg.CustomParams["PerStrategyPackIds"] = System.Text.Json.JsonSerializer.Serialize(req.PerStrategyPackIds);
        if (req.DisableRegime)
            cfg.CustomParams["DisableRegime"] = "true";
        // iter-redesign P3.2: "no add-ons (raw)" — strip every enrichment add-on for the whole run.
        if (req.StripAddOns)
            cfg.CustomParams["StripAddOns"] = "true";

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

    [HttpPost("delete")]
    public async Task<IActionResult> DeleteRuns([FromBody] DeleteRunsRequest? req, CancellationToken ct)
    {
        var ids = req?.RunIds?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList() ?? [];
        if (ids.Count == 0)
            return BadRequest(new { error = "No run ids supplied." });

        var active = ids.Where(id => _orchestrator.GetState(id)?.Status is "running" or "starting").ToList();
        if (active.Count > 0)
        {
            return Conflict(new
            {
                error = $"{active.Count} run(s) are still active — cancel them first.",
                activeRunIds = active,
            });
        }

        var deleted = await _runRepo.DeleteRunsAsync(ids, ct);
        foreach (var id in ids) _cache?.Evict(id);
        _logger.LogInformation("Deleted {Requested} run(s); {Deleted} headers removed.", ids.Count, deleted);
        return Ok(new { deleted });
    }

    [HttpPost("prune")]
    public async Task<IActionResult> Prune([FromBody] PruneRunsRequest? req, CancellationToken ct)
    {
        var keep = Math.Max(0, req?.Keep ?? 20);
        var all = await _runRepo.GetAllAsync(ct);
        var ordered = all.OrderByDescending(r => r.StartedAtUtc).ToList();
        var toDelete = ordered.Skip(keep)
            .Where(r => _orchestrator.GetState(r.RunId)?.Status is not ("running" or "starting"))
            .Select(r => r.RunId).ToList();

        if (toDelete.Count == 0)
            return Ok(new { deleted = 0, kept = ordered.Count });

        await _runRepo.DeleteRunsAsync(toDelete, ct);
        foreach (var id in toDelete) _cache?.Evict(id);
        _logger.LogInformation("Pruned {Deleted} run(s), kept newest {Keep}.", toDelete.Count, keep);
        return Ok(new { deleted = toDelete.Count, kept = ordered.Count - toDelete.Count });
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
            CommissionPerMillion = source.CommissionPerMillion,
            SpreadPips = source.SpreadPips,
        };

        if (req.StrategyIds is { Count: > 0 })
            cfg.CustomParams["StrategyIds"] = string.Join(",", req.StrategyIds.Select(s => s.Trim()));
        if (!string.IsNullOrWhiteSpace(req.RiskProfileId))
            cfg.CustomParams["RiskProfileId"] = req.RiskProfileId.Trim();
        if (!string.IsNullOrWhiteSpace(req.Venue))
            cfg.CustomParams["Venue"] = req.Venue.Trim().ToLowerInvariant();
        if (req.StrategyOverrides is { Count: > 0 })
            cfg.CustomParams["StrategyOverrides"] = System.Text.Json.JsonSerializer.Serialize(req.StrategyOverrides);
        if (!string.IsNullOrWhiteSpace(req.UsePackId))
            cfg.CustomParams["UsePackId"] = req.UsePackId.Trim();
        if (req.DisableRegime)
            cfg.CustomParams["DisableRegime"] = "true";
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
        return Ok(new { totalCount = trades.Count, trades });
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
        // iter-38 W-C2/B2: push the kind filter to the DB query (was applied client-side AFTER the limit,
        // so paging and kind-filtering together produced a misleading short page).
        var entries = await _journals.GetByRunAsync(runId, afterSeq, Math.Clamp(limit, 1, 1000), ct, kind);
        return Ok(entries);
    }

    // iter-38 B2: per-bar decisions for the "why" funnel (F2). Returns ONLY BarClosed StepRecords (which
    // carry StrategyVerdicts + regime), server-paged. De-noises: EquityObserved etc. are excluded by
    // the kind filter; the page size is reasonable for a per-bar view.
    [HttpGet("{runId}/bar-decisions")]
    public async Task<IActionResult> GetBarDecisions(
        string runId,
        [FromQuery] long? afterSeq,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var entries = await _journals.GetByRunAsync(runId, afterSeq, Math.Clamp(limit, 1, 500), ct, "BarClosed");
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
        opts.Converters.Add(new TradingEngine.Web.Configuration.Json.UtcDateTimeConverter()); // iter-38 W-B8: UTC 'Z'
        opts.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()); // enums as strings, matching the persisted journal
        await foreach (var entry in _journals.StreamByRunAsync(runId, afterSeq, ct))
        {
            await Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(entry, opts) + "\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    [HttpGet("{runId}/narrative")]
    public async Task<IActionResult> GetNarrative(
        string runId,
        [FromQuery] long? afterSeq,
        [FromQuery] string? kinds,
        [FromQuery] string? severity,
        [FromQuery] int limit = 100)
    {
        if (_narrative is null)
            return Problem("Narrative service not available.");

        var kindArray = kinds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = await _narrative.GetNarrativeAsync(runId, afterSeq, kindArray, severity, limit, HttpContext.RequestAborted);
        return Ok(new
        {
            events = result.Events,
            latestSeq = result.LatestSeq,
            hasMore = result.HasMore,
        });
    }

    [HttpGet("{runId}/equity")]
    public async Task<IActionResult> GetEquity(string runId, CancellationToken ct)
    {
        var points = await _query.GetRunEquityAsync(runId, ct);
        return Ok(points);
    }

    // iter-redesign P5.2: per-bar decision narrative — "what did the engine see, decide, and why" for
    // each bar, aggregated from the persisted journal (regime, strategy verdicts, proposals, gate
    // rejections with numeric reasons, and the equity/dd/open-book snapshot at that bar).
    [HttpGet("{runId}/bars")]
    public async Task<IActionResult> GetBars(string runId, DateTime? from, DateTime? to, CancellationToken ct)
    {
        var bars = await _query.GetRunBarsAsync(runId, from, to, ct);
        return Ok(bars);
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

    // iter-37 F2: per-strategy decision funnel (signals fired / top no-signal reasons / win-rate), built
    // from the StepRecord journal's per-bar verdicts (A3 / K-GAP-4) — the per-bar "why" the report surfaces.
    [HttpGet("{runId}/analytics/strategies")]
    public async Task<IActionResult> GetStrategyBreakdown(string runId, CancellationToken ct)
        => Ok(await _legacyQuery.GetStrategyBreakdownAsync(runId, ct));

    [HttpGet("/api/equity")]
    public async Task<IActionResult> GetEquity([FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var points = await _legacyQuery.GetEquityAsync(from, to, ct);
        return Ok(points.Select(p => new { p.TimestampUtc, p.Equity, p.Balance }));
    }

    // C2: one-page JSON export — all run data in a single machine-readable document.
    [HttpGet("{runId}/export/json")]
    public async Task<IActionResult> ExportJson(string runId, CancellationToken ct)
    {
        var run = await _query.GetRunAsync(runId, ct);
        if (run is null) return NotFound(new { error = $"Run {runId} not found" });

        var trades = await _query.GetRunTradesAsync(runId, ct);
        var equity = await _query.GetRunEquityAsync(runId, ct);
        var dailyPnl = await _query.GetRunDailyPnLAsync(runId, ct);
        var analytics = await _query.GetRunAnalyticsAsync(runId, ct);
        var breakdown = await _legacyQuery.GetStrategyBreakdownAsync(runId, ct);

        var costs = trades.Aggregate((Gross: 0m, Comm: 0m, Swap: 0m, Net: 0m),
            (a, t) => (a.Gross + t.GrossPnLAmount, a.Comm + t.CommissionAmount, a.Swap + t.SwapAmount, a.Net + t.NetPnLAmount));

        var result = new
        {
            run = new
            {
                run.RunId,
                run.Status,
                run.Symbol,
                run.Period,
                Symbols = ParseJsonArray(run.Symbols),
                Periods = ParseJsonArray(run.Periods),
                run.StartedAtUtc,
                run.CompletedAtUtc,
                BacktestFrom = run.BacktestFrom,
                BacktestTo = run.BacktestTo,
                run.InitialBalance,
                run.NetProfit,
                run.GrossPnL,
                run.CommissionTotal,
                run.SwapTotal,
                run.MaxDrawdownPct,
                run.TotalTrades,
                run.WinningTrades,
                run.WinRatePct,
            },
            costReconciliation = new
            {
                gross = costs.Gross,
                commission = costs.Comm,
                swap = costs.Swap,
                net = costs.Net,
                verified = costs.Gross - costs.Comm - costs.Swap == costs.Net,
            },
            trades,
            equity,
            dailyPnl,
            analytics,
            strategyBreakdown = breakdown,
        };

        return Ok(result);
    }
}


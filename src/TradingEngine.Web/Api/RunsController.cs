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
    private readonly BacktestOrchestrator _orchestrator;
    private readonly ILogger<RunsController> _logger;

    public RunsController(
        IRunQueryService query,
        IBacktestCommandService command,
        IBacktestQueryService legacyQuery,
        BacktestOrchestrator orchestrator,
        ILogger<RunsController> logger)
    {
        _query = query;
        _command = command;
        _legacyQuery = legacyQuery;
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
        var symList = !string.IsNullOrWhiteSpace(req.Symbols)
            ? req.Symbols.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.ToUpperInvariant()).ToArray()
            : new[] { req.Symbol.ToUpperInvariant() };

        var perList = !string.IsNullOrWhiteSpace(req.Periods)
            ? req.Periods.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.ToUpperInvariant()).ToArray()
            : new[] { req.Period.ToUpperInvariant() };

        var stratList = !string.IsNullOrWhiteSpace(req.StrategyIds)
            ? req.StrategyIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).ToArray()
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

        var runId = await _command.StartAsync(cfg, ct);
        var state = _orchestrator.GetState(runId);
        _logger.LogInformation("Run started. RunId={RunId}", runId);

        return Ok(new StartRunResponse { RunId = runId, Status = state?.Status ?? "started" });
    }

    [HttpDelete("{runId}")]
    public async Task<IActionResult> Cancel(string runId)
    {
        await _orchestrator.StopAllAsync();
        return Ok(new { cancelled = true });
    }

    [HttpGet("{runId}/trades")]
    public async Task<IActionResult> GetTrades(string runId, CancellationToken ct)
    {
        var trades = await _query.GetRunTradesAsync(runId, ct);
        return Ok(trades);
    }

    [HttpGet("{runId}/journal")]
    public async Task<IActionResult> GetJournal(
        string runId,
        [FromQuery] string? kind,
        [FromQuery] long? afterSeq,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var entries = await _query.GetRunJournalAsync(runId, kind, afterSeq, limit, ct);
        return Ok(entries);
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

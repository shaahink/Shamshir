using TradingEngine.CTraderRunner;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/backtest")]
public sealed class BacktestController : ControllerBase
{
    private readonly IBacktestCommandService _command;
    private readonly BacktestOrchestrator _orchestrator;
    private readonly BacktestProgressStore _progressStore;
    private readonly IPipelineEventRepository _pipelineRepo;
    private readonly ILogger<BacktestController> _logger;

    public BacktestController(
        IBacktestCommandService command,
        BacktestOrchestrator orchestrator,
        BacktestProgressStore progressStore,
        IPipelineEventRepository pipelineRepo,
        ILogger<BacktestController> logger)
    {
        _command = command;
        _orchestrator = orchestrator;
        _progressStore = progressStore;
        _pipelineRepo = pipelineRepo;
        _logger = logger;
    }

    public sealed record StartRequest
    {
        public string Symbol { get; init; } = "EURUSD";
        public string Period { get; init; } = "h1";
        public DateTime Start { get; init; } = new(2024, 1, 1);
        public DateTime End { get; init; } = new(2024, 1, 31);
        public decimal Balance { get; init; } = 100_000;
        public double CommissionPerMillion { get; init; } = 30;
        public double SpreadPips { get; init; } = 1;
        public string? Symbols { get; init; }
        public string? Periods { get; init; }
        public string? StrategyIds { get; init; }
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartRequest req)
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

        var runId = await _command.StartAsync(cfg, HttpContext.RequestAborted);
        var state = _orchestrator.GetState(runId);
        _logger.LogInformation("Backtest started. RunId={RunId} Symbol={Symbol} Period={Period}",
            runId, cfg.Symbol, cfg.Period);

        return Ok(new { runId, status = state?.Status ?? "unknown" });
    }

    private object? ResolveGovernor(BacktestOrchestrator.BacktestRunState state)
    {
        try
        {
            var host = state.EngineHost;
            if (host is null) return null;
            var gov = host.Services.GetRequiredService<ITradingGovernor>();
            var snap = gov.GetSnapshot();
            return new { state = snap.State.ToString(), reason = snap.Reason };
        }
        catch { return null; }
    }

    [HttpGet("{runId}/status")]
    public IActionResult Status(string runId)
    {
        var state = _orchestrator.GetState(runId);
        if (state is null)
            return NotFound(new { error = $"Run {runId} not found" });

        return Ok(new
        {
            runId = state.RunId,
            status = state.Status,
            startedAt = state.StartedAt,
            barCount = state.BarCount,
            simTime = state.SimTime,
            logs = state.GetLogs(),
            governor = ResolveGovernor(state),
            result = state.Result is not null ? new
            {
                state.Result.NetProfit,
                state.Result.MaxDrawdownPct,
                state.Result.TotalTrades,
                state.Result.WinningTrades,
                state.Result.WinRatePct,
                state.Result.Success,
            } : null,
            error = state.Error,
        });
    }

    [HttpGet("{runId}/logs")]
    public IActionResult Logs(string runId)
    {
        var state = _orchestrator.GetState(runId);
        if (state is null)
            return NotFound(new { error = $"Run {runId} not found" });

        return Ok(new { logs = state.GetLogs() });
    }

    [HttpGet("{runId}/journal")]
    public async Task<IActionResult> Journal(string runId)
    {
        var events = await _pipelineRepo.GetByRunIdAsync(runId, HttpContext.RequestAborted);
        var funnel = events
            .GroupBy(e => e.Stage)
            .OrderBy(g => g.Key)
            .Select(g => new { stage = g.Key, count = g.Count() })
            .ToList();

        return Ok(new { runId, funnel, events = events.Select(e => new
        {
            seq = e.Seq,
            stage = e.Stage,
            simTime = e.SimTimeUtc,
            correlationId = e.CorrelationId,
            detail = e.DetailJson
        }).ToList() });
    }

    // LEGACY / intentionally unconsumed: the live Monitor moved to SignalR (RunHub +
    // RunProgressBroadcaster) in iter-21. This SSE stream is retained only for ad-hoc curl debugging
    // of the raw journal channel; no page subscribes to it. Do not build new features on it.
    [HttpGet("{runId}/stream")]
    public async Task Stream(string runId, CancellationToken ct)
    {
        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("X-Accel-Buffering", "no");

        var reader = _progressStore.GetReader(runId);
        if (reader is null)
        {
            await Response.WriteAsync("data: {\"status\":\"not_found\"}\n\n", ct);
            return;
        }

        try
        {
            await foreach (var line in reader.ReadAllAsync(ct))
            {
                if (ct.IsCancellationRequested) break;
                await Response.WriteAsync($"data: {line}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}

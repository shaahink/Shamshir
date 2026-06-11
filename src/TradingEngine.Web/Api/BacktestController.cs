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
    private readonly ILogger<BacktestController> _logger;

    public BacktestController(
        IBacktestCommandService command,
        BacktestOrchestrator orchestrator,
        BacktestProgressStore progressStore,
        ILogger<BacktestController> logger)
    {
        _command = command;
        _orchestrator = orchestrator;
        _progressStore = progressStore;
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

        var runId = await _command.StartAsync(cfg, HttpContext.RequestAborted);
        var state = _orchestrator.GetState(runId);
        _logger.LogInformation("Backtest started. RunId={RunId} Symbol={Symbol} Period={Period}",
            runId, cfg.Symbol, cfg.Period);

        return Ok(new { runId, status = state?.Status ?? "unknown" });
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

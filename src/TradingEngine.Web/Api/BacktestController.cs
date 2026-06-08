using TradingEngine.CTraderRunner;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/backtest")]
public sealed class BacktestController : ControllerBase
{
    private readonly BacktestOrchestrator _orchestrator;
    private readonly ILogger<BacktestController> _logger;

    public BacktestController(BacktestOrchestrator orchestrator, ILogger<BacktestController> logger)
    {
        _orchestrator = orchestrator;
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
    }

    [HttpPost("start")]
    public IActionResult Start([FromBody] StartRequest req)
    {
        var cfg = new BacktestConfig
        {
            Symbol = req.Symbol.ToUpperInvariant(),
            Period = req.Period.ToLowerInvariant(),
            Start = req.Start,
            End = req.End,
            Balance = req.Balance,
            CommissionPerMillion = req.CommissionPerMillion,
            SpreadPips = req.SpreadPips,
        };

        var state = _orchestrator.Start(cfg);
        _logger.LogInformation("Backtest started. RunId={RunId} Symbol={Symbol} Period={Period}",
            state.RunId, cfg.Symbol, cfg.Period);

        return Ok(new { runId = state.RunId, status = state.Status });
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
        var state = _orchestrator.GetState(runId);
        if (state is null)
        {
            HttpContext.Response.StatusCode = 404;
            return;
        }

        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");

        var lastCount = 0;
        while (!ct.IsCancellationRequested && state.Status is "starting" or "running")
        {
            var logs = state.GetLogs();
            for (var i = lastCount; i < logs.Count; i++)
            {
                var json = JsonSerializer.Serialize(new { line = logs[i] });
                await Response.WriteAsync($"data: {json}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }
            lastCount = logs.Count;
            await Task.Delay(500, ct);
        }

        var finalLogs = state.GetLogs();
        for (var i = lastCount; i < finalLogs.Count; i++)
        {
            var json = JsonSerializer.Serialize(new { line = finalLogs[i] });
            await Response.WriteAsync($"data: {json}\n\n", ct);
        }
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(new { done = true, status = state.Status, error = state.Error })}\n\n", ct);
        await Response.Body.FlushAsync(ct);
    }
}

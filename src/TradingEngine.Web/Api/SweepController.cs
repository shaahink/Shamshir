using Microsoft.AspNetCore.Mvc;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/sweeps")]
public sealed class SweepController : ControllerBase
{
    private readonly SweepRunnerService _sweep;

    public SweepController(SweepRunnerService sweep)
    {
        _sweep = sweep;
    }

    [HttpPost("start")]
    public IActionResult Start([FromBody] SweepRequest request)
    {
        if (request.Strategies.Length == 0 || request.Symbols.Length == 0 || request.Timeframes.Length == 0)
            return BadRequest(new { error = "Strategies, symbols, and timeframes are required." });

        if (request.Parameters is null or { Count: 0 })
            return BadRequest(new { error = "At least one parameter with values is required." });

        var job = _sweep.Start(request);
        return Ok(new
        {
            jobId = job.Id,
            totalCells = job.TotalCells,
            status = job.Status,
            cells = ExpandPreview(request),
        });
    }

    [HttpGet("jobs/{jobId}")]
    public IActionResult GetJob(string jobId)
    {
        var job = _sweep.GetJob(jobId);
        if (job is null) return NotFound();
        return Ok(MapJob(job));
    }

    [HttpGet("jobs/{jobId}/results")]
    public IActionResult GetResults(string jobId)
    {
        var job = _sweep.GetJob(jobId);
        if (job is null) return NotFound();
        if (job.Results is null) return Ok(Array.Empty<object>());

        return Ok(job.Results.Select(r => new
        {
            cell = $"{r.Cell.StrategyId}|{r.Cell.Symbol}|{r.Cell.Timeframe}|{r.Cell.ParamKey}={r.Cell.ParamValue}",
            runId = r.RunId,
            netProfit = r.NetProfit,
            maxDrawdownPct = r.MaxDrawdownPct,
            totalTrades = r.TotalTrades,
            winningTrades = r.WinningTrades,
            winRatePct = r.WinRatePct,
            grossPnL = r.GrossPnL,
            commissionTotal = r.CommissionTotal,
            swapTotal = r.SwapTotal,
            totalBars = r.TotalBars,
            barsPerSec = r.BarsPerSec,
            wallElapsedMs = r.WallElapsedMs,
            error = r.Error,
        }));
    }

    [HttpGet("jobs/{jobId}/csv")]
    public IActionResult DownloadCsv(string jobId)
    {
        var job = _sweep.GetJob(jobId);
        if (job is null) return NotFound();
        if (job.Results is null) return NoContent();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("strategy,symbol,tf,paramKey,paramValue,runId,netProfit,maxDD,totalTrades,winRate,barsPerSec,error");
        foreach (var r in job.Results)
        {
            sb.AppendLine($"{r.Cell.StrategyId},{r.Cell.Symbol},{r.Cell.Timeframe},{r.Cell.ParamKey},{r.Cell.ParamValue},{r.RunId},{r.NetProfit},{r.MaxDrawdownPct},{r.TotalTrades},{r.WinRatePct:F2},{r.BarsPerSec:F0},{r.Error}");
        }

        return File(System.Text.Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"sweep-{jobId}.csv");
    }

    private static object MapJob(SweepJob job) => new
    {
        jobId = job.Id,
        job.Status,
        totalCells = job.TotalCells,
        completedCells = job.CompletedCells,
        createdAtUtc = job.CreatedAtUtc,
        completedAtUtc = job.CompletedAtUtc,
        request = job.Request is null ? null : new
        {
            job.Request.Strategies,
            job.Request.Symbols,
            job.Request.Timeframes,
            parameters = job.Request.Parameters?.ToDictionary(
                kv => kv.Key,
                kv => (object)kv.Value.Select(v => (double)v).ToList()),
            from = job.Request.From,
            to = job.Request.To,
            job.Request.Balance,
        },
    };

    private static List<object> ExpandPreview(SweepRequest req)
    {
        var cells = new List<object>();
        foreach (var strategy in req.Strategies)
        {
            foreach (var symbol in req.Symbols)
            {
                foreach (var tf in req.Timeframes)
                {
                    foreach (var param in req.Parameters)
                    {
                        foreach (var val in param.Value)
                        {
                            cells.Add(new
                            {
                                strategy,
                                symbol,
                                tf,
                                paramKey = param.Key,
                                paramValue = val,
                            });
                        }
                    }
                }
            }
        }
        return cells;
    }
}

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TradingEngine.CTraderRunner;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Web.Dtos.Runs;

namespace TradingEngine.Web.Services;

public sealed class SweepRunnerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SweepRunnerService> _logger;

    private readonly ConcurrentDictionary<string, SweepJob> _jobs = new();

    public SweepRunnerService(
        IServiceScopeFactory scopeFactory,
        ILogger<SweepRunnerService>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger!;
    }

    public SweepJob Start(SweepRequest request)
    {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        var cells = ExpandCells(request);
        var job = new SweepJob
        {
            Id = jobId,
            Request = request,
            Status = "running",
            TotalCells = cells.Count,
            CreatedAtUtc = DateTime.UtcNow,
        };
        _jobs[jobId] = job;

        _ = Task.Run(() => ExecuteAsync(job, cells));
        return job;
    }

    public SweepJob? GetJob(string jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job : null;

    public IReadOnlyList<SweepJob> ListJobs() =>
        [.. _jobs.Values.OrderByDescending(j => j.CreatedAtUtc)];

    private static List<SweepCell> ExpandCells(SweepRequest req)
    {
        var cells = new List<SweepCell>();
        foreach (var strategy in req.Strategies)
        {
            foreach (var symbol in req.Symbols)
            {
                foreach (var tf in req.Timeframes)
                {
                    foreach (var param in req.Parameters)
                    {
                        foreach (var paramValue in param.Value)
                        {
                            var cellIndex = cells.Count;
                            cells.Add(new SweepCell
                            {
                                Index = cellIndex,
                                StrategyId = strategy,
                                Symbol = symbol,
                                Timeframe = tf,
                                ParamKey = param.Key,
                                ParamValue = paramValue,
                            });
                        }
                    }
                }
            }
        }
        return cells;
    }

    private async Task ExecuteAsync(SweepJob job, List<SweepCell> cells)
    {
        var semaphore = new SemaphoreSlim(4);
        var results = new ConcurrentBag<SweepCellResult>();

        var tasks = cells.Select(cell => Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            try
            {
                var result = await ExecuteCellAsync(job, cell);
                results.Add(result);
                Interlocked.Increment(ref job.CompletedCells);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sweep cell {Index} failed: {Strategy} {Symbol} {Tf} {Key}={Value}",
                    cell.Index, cell.StrategyId, cell.Symbol, cell.Timeframe, cell.ParamKey, cell.ParamValue);
                results.Add(new SweepCellResult { Cell = cell, Error = ex.Message });
                Interlocked.Increment(ref job.CompletedCells);
            }
            finally
            {
                semaphore.Release();
            }
        }));

        await Task.WhenAll(tasks);

        job.Results = results.OrderBy(r => r.Cell.Index).ToList();
        job.Status = "done";
        job.CompletedAtUtc = DateTime.UtcNow;
    }

    private async Task<SweepCellResult> ExecuteCellAsync(SweepJob job, SweepCell cell)
    {
        if (job.Request is null)
            return new SweepCellResult { Cell = cell, Error = "Sweep request is null." };

        var req = job.Request;
        var config = new BacktestConfig
        {
            Symbol = cell.Symbol,
            Period = cell.Timeframe,
            Start = req.From,
            End = req.To,
            Balance = req.Balance,
            CommissionPerMillion = req.CommissionPerMillion,
            SpreadPips = req.SpreadPips,
            Symbols = [cell.Symbol],
            Periods = [cell.Timeframe],
            CustomParams = new Dictionary<string, string>(req.CustomParams)
            {
                ["Venue"] = "tape",
                ["StrategyIds"] = cell.StrategyId,
                [cell.ParamKey] = cell.ParamValue.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
                ["SweepJobId"] = job.Id,
                ["SkipJournal"] = "true",
            },
        };

        using var scope = _scopeFactory.CreateScope();
        var command = scope.ServiceProvider.GetRequiredService<IBacktestCommandService>();
        var runQuery = scope.ServiceProvider.GetRequiredService<IRunQueryService>();
        var db = scope.ServiceProvider.GetRequiredService<TradingEngine.Infrastructure.Persistence.TradingDbContext>();

        // Content-address skip: if a completed run exists for this exact cell, reuse it.
        var existing = await db.BacktestRuns.AsNoTracking()
            .Where(r => r.Symbol == cell.Symbol && r.Period == cell.Timeframe
                && r.BacktestFrom == req.From && r.BacktestTo == req.To
                && r.Venue == "tape" && r.CompletedAtUtc != default
                && r.TotalTrades > 0)
            .OrderByDescending(r => r.CompletedAtUtc)
            .FirstOrDefaultAsync(CancellationToken.None);

        if (existing is not null)
        {
            var skipDetail = await runQuery.GetRunAsync(existing.RunId, CancellationToken.None);
            if (skipDetail is not null)
            {
                return new SweepCellResult
                {
                    Cell = cell,
                    RunId = existing.RunId,
                    NetProfit = skipDetail.NetProfit,
                    MaxDrawdownPct = skipDetail.MaxDrawdownPct,
                    TotalTrades = skipDetail.TotalTrades,
                    WinningTrades = skipDetail.WinningTrades,
                    WinRatePct = skipDetail.WinRatePct,
                    GrossPnL = skipDetail.GrossPnL,
                    CommissionTotal = skipDetail.CommissionTotal,
                    SwapTotal = skipDetail.SwapTotal,
                    TotalBars = skipDetail.TotalBars,
                    BarsPerSec = skipDetail.BarsPerSec,
                    WallElapsedMs = skipDetail.WallElapsedMs,
                };
            }
        }

        var runId = await command.StartAsync(config, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(runId))
            return new SweepCellResult { Cell = cell, Error = "Failed to start run." };

        // Poll for completion
        RunDetailResponse? detail = null;
        for (var i = 0; i < 300; i++)
        {
            await Task.Delay(500);
            detail = await runQuery.GetRunAsync(runId, CancellationToken.None);
            if (detail is null) continue;
            if (detail.Status is "completed" or "failed" or "cancelled")
                break;
        }

        if (detail is null)
            return new SweepCellResult { Cell = cell, RunId = runId, Error = "Timed out waiting for run." };

        return new SweepCellResult
        {
            Cell = cell,
            RunId = runId,
            NetProfit = detail.NetProfit,
            MaxDrawdownPct = detail.MaxDrawdownPct,
            TotalTrades = detail.TotalTrades,
            WinningTrades = detail.WinningTrades,
            WinRatePct = detail.WinRatePct,
            GrossPnL = detail.GrossPnL,
            CommissionTotal = detail.CommissionTotal,
            SwapTotal = detail.SwapTotal,
            TotalBars = detail.TotalBars,
            BarsPerSec = detail.BarsPerSec,
            WallElapsedMs = detail.WallElapsedMs,
            Error = detail.ErrorMessage,
        };
    }
}

public sealed class SweepRequest
{
    public string[] Strategies { get; init; } = [];
    public string[] Symbols { get; init; } = [];
    public string[] Timeframes { get; init; } = [];
    public Dictionary<string, decimal[]> Parameters { get; init; } = new();
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public decimal Balance { get; init; } = 50_000;
    public double CommissionPerMillion { get; init; } = 30;
    public double SpreadPips { get; init; } = 1;
    public Dictionary<string, string> CustomParams { get; init; } = new();
}

public sealed class SweepJob
{
    public string Id { get; set; } = "";
    public SweepRequest? Request { get; set; }
    public string Status { get; set; } = "queued";
    public int TotalCells { get; set; }
    public int CompletedCells;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public IReadOnlyList<SweepCellResult>? Results { get; set; }
}

public sealed class SweepCell
{
    public int Index { get; init; }
    public string StrategyId { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string Timeframe { get; init; } = "";
    public string ParamKey { get; init; } = "";
    public decimal ParamValue { get; init; }
}

public sealed class SweepCellResult
{
    public SweepCell Cell { get; init; } = new();
    public string? RunId { get; init; }
    public decimal NetProfit { get; init; }
    public decimal MaxDrawdownPct { get; init; }
    public int TotalTrades { get; init; }
    public int WinningTrades { get; init; }
    public double WinRatePct { get; init; }
    public decimal GrossPnL { get; init; }
    public decimal CommissionTotal { get; init; }
    public decimal SwapTotal { get; init; }
    public int TotalBars { get; init; }
    public double BarsPerSec { get; init; }
    public long WallElapsedMs { get; init; }
    public string? Error { get; init; }
}

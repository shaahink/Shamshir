using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain.Experiments;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Web.Hubs;

namespace TradingEngine.Web.Services;

public sealed class WalkForwardBackgroundService : BackgroundService
{
    private readonly Channel<WalkForwardJobEntity> _queue = Channel.CreateBounded<WalkForwardJobEntity>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.Wait });
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHubContext<WalkForwardHub> _hub;
    private readonly ILogger<WalkForwardBackgroundService> _logger;

    public WalkForwardBackgroundService(IServiceScopeFactory scopeFactory, IHubContext<WalkForwardHub> hub, ILogger<WalkForwardBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    public void Enqueue(WalkForwardJobEntity job)
    {
        if (!_queue.Writer.TryWrite(job))
            _logger.LogWarning("Walk-forward queue full, job {JobId} rejected", job.Id);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ExecuteJobAsync(job, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Walk-forward job {JobId} failed", job.Id);
                await MarkFailedAsync(job.Id, ex.Message);
                try { await _hub.Clients.Group($"wf:{job.Id}").SendAsync("JobFailed", job.Id, ex.Message, cancellationToken: stoppingToken); } catch { }
            }
        }
    }

    private async Task ExecuteJobAsync(WalkForwardJobEntity job, CancellationToken ct)
    {
        var spec = JsonSerializer.Deserialize<WalkForwardSpec>(job.SpecJson);
        if (spec is null) { await MarkFailedAsync(job.Id, "Invalid spec"); return; }

        await UpdateStatusAsync(job.Id, "running", ct);

        var windows = WalkForwardSplitter.Split(spec.From, spec.To, spec).ToList();
        job.TotalWindows = windows.Count;
        await SaveJobAsync(job);

        for (var i = 0; i < windows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (trainFrom, trainTo, testFrom, testTo) = windows[i];

            using var scope = _scopeFactory.CreateScope();
            var sweepRunner = scope.ServiceProvider.GetRequiredService<SweepRunnerService>();

            var sweepRequest = BuildSweepRequest(spec, trainFrom, trainTo);
            var sweepJob = sweepRunner.Start(sweepRequest);
            while (sweepJob.Status is "pending" or "running" && !ct.IsCancellationRequested)
                await Task.Delay(500, ct);

            var windowResult = new WalkForwardWindowResultEntity
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                WindowIndex = i,
                TrainFromUtc = new DateTime(trainFrom, TimeOnly.MinValue, DateTimeKind.Utc),
                TrainToUtc = new DateTime(trainTo, TimeOnly.MaxValue, DateTimeKind.Utc),
                TestFromUtc = new DateTime(testFrom, TimeOnly.MinValue, DateTimeKind.Utc),
                TestToUtc = new DateTime(testTo, TimeOnly.MaxValue, DateTimeKind.Utc),
                StrategyId = spec.Strategies?.FirstOrDefault() ?? "unknown",
                Symbol = spec.Symbols?.FirstOrDefault() ?? "EURUSD",
                Timeframe = spec.Timeframes?.FirstOrDefault() ?? "H1",
                TrialsCount = sweepJob.TotalCells,
            };

            if (sweepJob.Results is { Count: > 0 })
            {
                var best = PickPlateauCell(sweepJob.Results);
                if (best is not null)
                {
                    windowResult.ChosenParamsJson = JsonSerializer.Serialize(new { best.Cell.ParamKey, best.Cell.ParamValue });
                    windowResult.PlateauValue = best.NetProfit > 0m ? (double)best.NetProfit : (double)best.MaxDrawdownPct;
                    windowResult.TestNetProfit = best.NetProfit;
                    windowResult.TestTotalTrades = best.TotalTrades;
                    windowResult.TestWinRatePct = best.WinRatePct;
                }
            }

            job.CompletedWindows = i + 1;
            await SaveJobWithWindowAsync(job, windowResult, ct);

            await _hub.Clients.Group($"wf:{job.Id}").SendAsync("WindowCompleted", new
            {
                jobId = job.Id,
                windowIndex = i,
                totalWindows = windows.Count,
                chosenParams = windowResult.ChosenParamsJson,
                testNetProfit = windowResult.TestNetProfit,
                testTotalTrades = windowResult.TestTotalTrades,
                trialsCount = windowResult.TrialsCount,
            }, cancellationToken: ct);
        }

        await UpdateStatusAsync(job.Id, "completed", ct);
        await _hub.Clients.Group($"wf:{job.Id}").SendAsync("JobCompleted", job.Id, cancellationToken: ct);
    }

    private static SweepCellResult? PickPlateauCell(IReadOnlyList<SweepCellResult> results)
    {
        return results.MaxBy(r => (double)(r.NetProfit > 0m ? r.NetProfit + (decimal)(r.WinRatePct * 1000) : -r.MaxDrawdownPct));
    }

    private static SweepRequest BuildSweepRequest(WalkForwardSpec spec, DateOnly from, DateOnly to)
    {
        return new SweepRequest
        {
            Strategies = spec.Strategies ?? [],
            Symbols = spec.Symbols ?? [],
            Timeframes = spec.Timeframes ?? [],
            Parameters = spec.ParamGrid ?? new Dictionary<string, decimal[]>(),
            From = new DateTime(from, TimeOnly.MinValue),
            To = new DateTime(to, TimeOnly.MaxValue),
            Balance = spec.Balance > 0 ? spec.Balance : 100_000m,
            CustomParams = new Dictionary<string, string> { ["RecordExcursions"] = "false" },
        };
    }

    private async Task SaveJobAsync(WalkForwardJobEntity job)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var existing = await db.Set<WalkForwardJobEntity>().FindAsync(job.Id);
        if (existing is not null)
        {
            existing.Status = job.Status;
            existing.TotalWindows = job.TotalWindows;
            existing.CompletedWindows = job.CompletedWindows;
        }
        else
        {
            db.Set<WalkForwardJobEntity>().Add(job);
        }
        await db.SaveChangesAsync();
    }

    private async Task SaveJobWithWindowAsync(WalkForwardJobEntity job, WalkForwardWindowResultEntity window, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var existing = await db.Set<WalkForwardJobEntity>().FindAsync(new object[] { job.Id }, ct);
        if (existing is not null) { existing.CompletedWindows = job.CompletedWindows; existing.Status = job.Status; }
        db.Set<WalkForwardWindowResultEntity>().Add(window);
        await db.SaveChangesAsync(ct);
    }

    private async Task UpdateStatusAsync(Guid jobId, string status, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var job = await db.Set<WalkForwardJobEntity>().FindAsync(new object[] { jobId }, ct);
        if (job is not null) { job.Status = status; await db.SaveChangesAsync(ct); }
    }

    private async Task MarkFailedAsync(Guid jobId, string error)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var job = await db.Set<WalkForwardJobEntity>().FindAsync(jobId);
        if (job is not null) { job.Status = "failed"; job.ErrorMessage = error; await db.SaveChangesAsync(); }
    }
}

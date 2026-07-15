using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TradingEngine.CTraderRunner;
using TradingEngine.Domain.Experiments;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Services.Helpers;
using TradingEngine.Web.Dtos.Runs;
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
        {
            _logger.LogWarning("Walk-forward queue full, job {JobId} marked failed", job.Id);
            _ = MarkFailedAsync(job.Id, "Queue full — try again later.");
        }
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

            // --- Train window: sweep the param grid ---
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

            // --- Plateau-pick the best train cell (P4.5.1: real plateau, not MaxBy) ---
            if (sweepJob.Results is { Count: > 0 })
            {
                var plateauCells = sweepJob.Results
                    .Select(r => new PlateauCell(
                        r.Cell.ParamKey,
                        r.Cell.ParamValue,
                        r.NetProfit,
                        r.WinRatePct,
                        r.MaxDrawdownPct,
                        r.Error))
                    .ToList();

                var best = PlateauPicker.Pick(plateauCells);
                if (best is { } b)
                {
                    windowResult.ChosenParamsJson = JsonSerializer.Serialize(new { b.ParamKey, b.ParamValue });
                    windowResult.PlateauValue = (double)b.NetProfit;
                }
            }

            // --- Test window (P4.5.1: run the test leg — was entirely missing) ---
            if (!string.IsNullOrWhiteSpace(windowResult.ChosenParamsJson))
            {
                var testResult = await RunTestWindowAsync(spec, windowResult, ct);
                if (testResult is not null)
                {
                    windowResult.TestRunId = testResult.RunId;
                    windowResult.TestNetProfit = testResult.NetProfit;
                    windowResult.TestTotalTrades = testResult.TotalTrades;
                    windowResult.TestWinRatePct = testResult.WinRatePct;
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
                testWinRate = windowResult.TestWinRatePct,
                trialsCount = windowResult.TrialsCount,
            }, cancellationToken: ct);
        }

        await UpdateStatusAsync(job.Id, "completed", ct);
        await _hub.Clients.Group($"wf:{job.Id}").SendAsync("JobCompleted", job.Id, cancellationToken: ct);
    }

    /// <summary>
    /// P4.5.1: runs a single backtest over the test window with the frozen best-cell params.
    /// Prior to this fix, the "OOS equity curve" was stitched in-sample maxima.
    /// </summary>
    private async Task<SweepCellResult?> RunTestWindowAsync(WalkForwardSpec spec, WalkForwardWindowResultEntity window, CancellationToken ct)
    {
        var chosen = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(window.ChosenParamsJson);
        if (chosen is null || !chosen.TryGetValue("ParamKey", out var pk) || !chosen.TryGetValue("ParamValue", out var pv))
            return null;

        var paramKey = pk.GetString();
        if (string.IsNullOrEmpty(paramKey)) return null;
        var paramValue = pv.GetDecimal();

        using var scope = _scopeFactory.CreateScope();
        var command = scope.ServiceProvider.GetRequiredService<IBacktestCommandService>();
        var runQuery = scope.ServiceProvider.GetRequiredService<IRunQueryService>();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        // Preload indicator warmup: request data from BEFORE testFrom so Skender
        // indicators are fully computed by the time the test window starts. Grab
        // enough bars to cover the maximum indicators' warmup period (~200 bars).
        var warmupFrom = window.TestFromUtc.AddDays(-60); // generous — 2 months of daily data

        var testCustomParams = new Dictionary<string, string>
        {
            ["Venue"] = "tape",
            ["StrategyIds"] = window.StrategyId,
            [paramKey] = paramValue.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
            ["SkipJournal"] = "true",
            ["TestWindowRun"] = "true",
        };
        if (!string.IsNullOrWhiteSpace(spec.PackId)) testCustomParams["UsePackId"] = spec.PackId;
        if (!string.IsNullOrWhiteSpace(spec.RiskProfileId)) testCustomParams["RiskProfileId"] = spec.RiskProfileId;

        var config = new BacktestConfig
        {
            Symbol = window.Symbol,
            Period = window.Timeframe,
            Start = new DateTime(warmupFrom.Year, warmupFrom.Month, warmupFrom.Day, warmupFrom.Hour, warmupFrom.Minute, warmupFrom.Second, DateTimeKind.Utc),
            End = window.TestToUtc,
            Balance = spec.Balance > 0 ? spec.Balance : 100_000m,
            Symbols = [window.Symbol],
            Periods = [window.Timeframe],
            CustomParams = testCustomParams,
        };

        var runId = await command.StartAsync(config, ct);
        if (string.IsNullOrWhiteSpace(runId)) return null;

        RunDetailResponse? detail = null;
        for (var i = 0; i < 300; i++)
        {
            await Task.Delay(500, ct);
            detail = await runQuery.GetRunAsync(runId, ct);
            if (detail is null) continue;
            if (detail.Status is "completed" or "failed" or "cancelled")
                break;
        }

        if (detail is null) return null;

        return new SweepCellResult
        {
            Cell = new SweepCell { StrategyId = window.StrategyId, Symbol = window.Symbol, Timeframe = window.Timeframe },
            RunId = runId,
            NetProfit = detail.NetProfit,
            TotalTrades = detail.TotalTrades,
            WinRatePct = detail.WinRatePct,
            MaxDrawdownPct = detail.MaxDrawdownPct,
            TotalBars = detail.TotalBars,
            WallElapsedMs = detail.WallElapsedMs,
            Error = detail.ErrorMessage,
        };
    }

    private static SweepRequest BuildSweepRequest(WalkForwardSpec spec, DateOnly from, DateOnly to)
    {
        var customParams = new Dictionary<string, string> { ["RecordExcursions"] = "false" };
        if (!string.IsNullOrWhiteSpace(spec.PackId)) customParams["UsePackId"] = spec.PackId;
        if (!string.IsNullOrWhiteSpace(spec.RiskProfileId)) customParams["RiskProfileId"] = spec.RiskProfileId;

        return new SweepRequest
        {
            Strategies = spec.Strategies ?? [],
            Symbols = spec.Symbols ?? [],
            Timeframes = spec.Timeframes ?? [],
            Parameters = spec.ParamGrid ?? new Dictionary<string, decimal[]>(),
            From = new DateTime(from, TimeOnly.MinValue),
            To = new DateTime(to, TimeOnly.MaxValue),
            Balance = spec.Balance > 0 ? spec.Balance : 100_000m,
            CustomParams = customParams,
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

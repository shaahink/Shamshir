using Microsoft.Extensions.Options;
using TradingEngine.Infrastructure.MarketData.Sync;
using TradingEngine.Web.Api;

namespace TradingEngine.Web.Services;

/// <summary>Options for <see cref="AutoSyncService"/>, section <c>MarketData:AutoSync</c>.</summary>
public sealed class AutoSyncOptions
{
    /// <summary>On by default. Inert until the watchlist has cells, so it never surprises with downloads.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often to reconcile coverage against "now".</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>Grace period after startup before the first tick (let the app settle).</summary>
    public int StartupDelaySeconds { get; set; } = 20;

    /// <summary>Per-download hard timeout so a hung cTrader session cannot wedge the loop.</summary>
    public int JobTimeoutSeconds { get; set; } = 1800;
}

/// <summary>
/// X4 — the on-by-default background auto-sync loop. Each tick it recomputes what the enabled watchlist
/// cells are missing from LIVE coverage (<see cref="MarketDataCoverageService"/>) and drives the existing,
/// now-consolidated download path (<see cref="DownloadJobService"/> → shared cTrader owner lane, dynamic
/// ports, idempotent ingest) to fill the gap. Because the decision is derived from durable DB coverage and
/// ingest is <c>INSERT OR IGNORE</c>, a restart mid-sync simply recomputes the still-missing range and
/// re-fills it — self-healing, no stuck jobs. It never enqueues a cell that already has an active job.
/// </summary>
public sealed class AutoSyncService(
    MarketDataCoverageService coverage,
    MarketDataSyncStore watchlist,
    DownloadJobService downloads,
    IOptions<AutoSyncOptions> options,
    ILogger<AutoSyncService> logger) : BackgroundService
{
    private readonly AutoSyncOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opts.Enabled)
        {
            logger.LogInformation("AUTOSYNC|disabled (MarketData:AutoSync:Enabled=false)");
            return;
        }

        logger.LogInformation("AUTOSYNC|starting — interval={Interval}m", _opts.IntervalMinutes);
        try { await Task.Delay(TimeSpan.FromSeconds(_opts.StartupDelaySeconds), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var started = await TickAsync(ct);
                if (started > 0)
                {
                    logger.LogInformation("AUTOSYNC|tick started {Count} fill job(s)", started);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "AUTOSYNC|tick failed — will retry next interval"); }

            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _opts.IntervalMinutes)), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// One reconcile pass: enqueue a fill for every enabled watchlist cell whose live coverage is behind,
    /// unless a job for that cell is already in flight. Returns the number of fills started. Also invoked
    /// on demand by the "Sync now" endpoint.
    /// </summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        var enabled = await watchlist.ListEnabledAsync(ct);
        if (enabled.Count == 0) return 0;

        var coverageRows = await coverage.GetCoverageAsync(ct);
        var byKey = coverageRows.ToDictionary(c => (c.Symbol, c.Timeframe));

        var active = downloads.List()
            .Where(j => j.Status is not ("done" or "failed"))
            .ToList();

        var started = 0;
        foreach (var cell in enabled)
        {
            var key = (cell.Symbol.ToUpperInvariant(), cell.Timeframe.ToLowerInvariant());
            if (!byKey.TryGetValue(key, out var cov) || cov.SyncFromUtc is null || cov.SyncToUtc is null)
            {
                continue; // up to date (or not resolvable)
            }

            var alreadyActive = active.Any(j =>
                string.Equals(j.Symbol, cell.Symbol, StringComparison.OrdinalIgnoreCase)
                && j.Timeframes.Any(t => string.Equals(t, cell.Timeframe, StringComparison.OrdinalIgnoreCase)));
            if (alreadyActive) continue;

            logger.LogInformation("AUTOSYNC|fill {Symbol} {Tf} {From:yyyy-MM-dd}..{To:yyyy-MM-dd} (status={Status})",
                cell.Symbol, cell.Timeframe, cov.SyncFromUtc, cov.SyncToUtc, cov.Status);

            downloads.Start(cell.Symbol, [cell.Timeframe], days: 0,
                from: cov.SyncFromUtc, to: cov.SyncToUtc,
                keepShards: false, timeoutSeconds: _opts.JobTimeoutSeconds);
            started++;
        }
        return started;
    }
}

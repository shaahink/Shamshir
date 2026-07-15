using Microsoft.AspNetCore.Mvc;
using TradingEngine.Infrastructure.MarketData;
using TradingEngine.Infrastructure.MarketData.Sync;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/data-manager")]
public sealed class DataManagerController : ControllerBase
{
    private readonly IMarketDataStore? _marketDataStore;
    private readonly DownloadJobService? _downloadJobs;
    private readonly ReferenceScalePopulator? _referenceScales;
    private readonly DataQualityValidator? _qualityValidator;
    private readonly MarketDataCoverageService? _coverage;
    private readonly MarketDataSyncStore? _watchlist;
    private readonly AutoSyncService? _autoSync;
    private readonly ILogger<DataManagerController> _logger;

    public DataManagerController(
        IMarketDataStore? marketDataStore = null,
        DownloadJobService? downloadJobs = null,
        ReferenceScalePopulator? referenceScales = null,
        DataQualityValidator? qualityValidator = null,
        MarketDataCoverageService? coverage = null,
        MarketDataSyncStore? watchlist = null,
        AutoSyncService? autoSync = null,
        ILogger<DataManagerController>? logger = null)
    {
        _marketDataStore = marketDataStore;
        _downloadJobs = downloadJobs;
        _referenceScales = referenceScales;
        _qualityValidator = qualityValidator;
        _coverage = coverage;
        _watchlist = watchlist;
        _autoSync = autoSync;
        _logger = logger!;
    }

    // ── X4: coverage view + auto-sync watchlist ──────────────────────────────────────────────────

    [HttpGet("coverage")]
    public async Task<IActionResult> GetCoverage(CancellationToken ct)
    {
        if (_coverage is null) return Ok(Array.Empty<object>());
        var rows = await _coverage.GetCoverageAsync(ct);
        return Ok(rows);
    }

    [HttpGet("watchlist")]
    public async Task<IActionResult> GetWatchlist(CancellationToken ct)
    {
        if (_watchlist is null) return Ok(Array.Empty<object>());
        var cells = await _watchlist.ListAsync(ct);
        return Ok(cells);
    }

    [HttpPost("watchlist")]
    public async Task<IActionResult> UpsertWatchlist([FromBody] WatchlistUpsertRequest req, CancellationToken ct)
    {
        if (_watchlist is null) return Problem("Sync store not registered.");
        if (string.IsNullOrWhiteSpace(req.Symbol) || string.IsNullOrWhiteSpace(req.Timeframe))
            return BadRequest(new { error = "Symbol and timeframe required." });

        var from = req.BackfillFromUtc is { } f
            ? DateTime.SpecifyKind(f, DateTimeKind.Utc)
            : new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await _watchlist.UpsertAsync(req.Symbol, req.Timeframe, from, req.Enabled ?? true, ct);
        return Ok(new { ok = true });
    }

    [HttpPost("watchlist/toggle")]
    public async Task<IActionResult> ToggleWatchlist([FromBody] WatchlistToggleRequest req, CancellationToken ct)
    {
        if (_watchlist is null) return Problem("Sync store not registered.");
        await _watchlist.SetEnabledAsync(req.Symbol, req.Timeframe, req.Enabled, ct);
        return Ok(new { ok = true });
    }

    [HttpPost("watchlist/remove")]
    public async Task<IActionResult> RemoveWatchlist([FromBody] WatchlistToggleRequest req, CancellationToken ct)
    {
        if (_watchlist is null) return Problem("Sync store not registered.");
        await _watchlist.RemoveAsync(req.Symbol, req.Timeframe, ct);
        return Ok(new { ok = true });
    }

    [HttpPost("sync-now")]
    public async Task<IActionResult> SyncNow(CancellationToken ct)
    {
        if (_autoSync is null) return Problem("Auto-sync service not registered.");
        var started = await _autoSync.TickAsync(ct);
        return Ok(new { started });
    }

    public sealed record WatchlistUpsertRequest
    {
        public string Symbol { get; init; } = "";
        public string Timeframe { get; init; } = "";
        public DateTime? BackfillFromUtc { get; init; }
        public bool? Enabled { get; init; }
    }

    public sealed record WatchlistToggleRequest
    {
        public string Symbol { get; init; } = "";
        public string Timeframe { get; init; } = "";
        public bool Enabled { get; init; }
    }

    [HttpGet("inventory")]
    public async Task<IActionResult> GetInventory(CancellationToken ct)
    {
        if (_marketDataStore is null)
            return Ok(Array.Empty<object>());

        var inventory = await _marketDataStore.GetInventoryAsync(ct);
        return Ok(inventory.Select(i => new
        {
            symbol = i.Symbol,
            timeframe = i.Timeframe.ToString(),
            source = i.Source,
            firstBar = i.FirstOpenUtc,
            lastBar = i.LastOpenUtc,
            barCount = i.BarCount,
        }));
    }

    [HttpPost("download")]
    public IActionResult StartDownload([FromBody] DownloadRequest req)
    {
        var symbols = (req.Symbols is { Length: > 0 } ? req.Symbols : [req.Symbol])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (symbols.Length == 0 || req.Tfs is null or { Length: 0 })
            return BadRequest(new { error = "At least one symbol and timeframe required." });

        if (_downloadJobs is null)
            return Problem("Download job service not registered.");

        DateTime? from = null, to = null;
        if (req.From is not null) from = DateTime.SpecifyKind(req.From.Value, DateTimeKind.Utc);
        if (req.To is not null) to = DateTime.SpecifyKind(req.To.Value, DateTimeKind.Utc);

        if (req.Days < 0)
            from = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var jobs = new List<object>();
        foreach (var sym in symbols)
        {
            var job = _downloadJobs.Start(sym, req.Tfs, req.Days, from, to, keepShards: req.KeepShards, timeoutSeconds: 1800);
            jobs.Add(new { jobId = job.Id, symbol = job.Symbol, tfs = job.Timeframes, status = job.Status });
        }

        return Ok(new { jobs });
    }

    [HttpGet("jobs")]
    public IActionResult ListJobs()
    {
        if (_downloadJobs is null) return Ok(Array.Empty<object>());
        var jobs = _downloadJobs.List().Select(MapJob);
        return Ok(jobs);
    }

    [HttpGet("jobs/{jobId}")]
    public IActionResult GetJob(string jobId)
    {
        if (_downloadJobs is null) return NotFound();
        var job = _downloadJobs.Get(jobId);
        if (job is null) return NotFound();
        return Ok(MapJob(job));
    }

    [HttpPost("delete")]
    public async Task<IActionResult> DeleteBars([FromBody] DeleteBarsRequest req, CancellationToken ct)
    {
        if (_marketDataStore is null)
            return Problem("Market data store not registered.");
        if (string.IsNullOrWhiteSpace(req.Symbol) || string.IsNullOrWhiteSpace(req.Timeframe))
            return BadRequest(new { error = "Symbol and timeframe required." });
        if (!Enum.TryParse<Timeframe>(req.Timeframe, ignoreCase: true, out var tf))
            return BadRequest(new { error = $"Unknown timeframe '{req.Timeframe}'." });

        DateTime? from = req.From is null ? null : DateTime.SpecifyKind(req.From.Value, DateTimeKind.Utc);
        DateTime? to = req.To is null ? null : DateTime.SpecifyKind(req.To.Value, DateTimeKind.Utc);

        var deleted = await _marketDataStore.DeleteBarsAsync(Symbol.Parse(req.Symbol), tf, from, to, req.Source, ct);
        _logger.LogInformation("Deleted {Deleted} market-data bar(s) for {Symbol} {Tf}.", deleted, req.Symbol, req.Timeframe);
        return Ok(new { deleted });
    }

    [HttpGet("pending-shards")]
    public IActionResult GetPendingShards()
    {
        if (_downloadJobs is null)
            return Ok(new { shardsRoot = "", files = Array.Empty<object>() });

        var root = _downloadJobs.ShardsRoot;
        var result = new List<object>();

        if (Directory.Exists(root))
        {
            foreach (var file in Directory.GetFiles(root, "*.ndjson", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file);
                if (rel.StartsWith("archive", StringComparison.OrdinalIgnoreCase))
                    continue;

                var fi = new FileInfo(file);
                var relativePath = rel;

                var symbol = "";
                var timeframe = "";
                var name = Path.GetFileNameWithoutExtension(file);
                var parts = name.Split('_');
                if (parts.Length >= 2)
                {
                    symbol = parts[0].ToUpperInvariant();
                    timeframe = parts[1].ToLowerInvariant();
                }

                result.Add(new
                {
                    fileName = Path.GetFileName(file),
                    relativePath,
                    symbol,
                    timeframe,
                    sizeBytes = fi.Length,
                    lastModifiedUtc = fi.LastWriteTimeUtc,
                });
            }
        }

        return Ok(new { shardsRoot = root, files = result });
    }

    [HttpPost("ingest-shards")]
    public IActionResult IngestShards([FromBody] IngestShardsRequest? req)
    {
        if (_marketDataStore is null)
            return Problem("Market data store not registered.");
        if (_downloadJobs is null)
            return Problem("Download job service not registered.");

        var job = _downloadJobs.StartIngest(req?.Symbol);
        return Ok(new
        {
            jobId = job.Id,
            symbol = job.Symbol,
            status = job.Status,
        });
    }

    [HttpPost("compute-reference-scales")]
    public async Task<IActionResult> ComputeReferenceScales(CancellationToken ct)
    {
        if (_referenceScales is null)
            return Problem("Reference scale populator not registered.");
        if (_marketDataStore is null)
            return Problem("Market data store not registered.");

        var updated = await _referenceScales.PopulateAllAsync(ct);
        return Ok(new { cellsUpdated = updated });
    }

    [HttpGet("quality-report")]
    public async Task<IActionResult> GetQualityReport(CancellationToken ct)
    {
        if (_qualityValidator is null)
            return Problem("Data quality validator not registered.");
        if (_marketDataStore is null)
            return Problem("Market data store not registered.");

        var report = await _qualityValidator.GenerateReportAsync(ct);
        return Ok(report);
    }

    private static object MapJob(DownloadJob job) => new
    {
        jobId = job.Id,
        job.Symbol,
        tfs = job.Timeframes,
        job.Days,
        from = job.From,
        to = job.To,
        job.Status,
        createdAtUtc = job.CreatedAtUtc,
        startedAtUtc = job.StartedAtUtc,
        completedAtUtc = job.CompletedAtUtc,
        barsRecorded = job.BarsRecorded,
        linesProcessed = job.LinesProcessed,
        filesTotal = job.FilesTotal,
        filesProcessed = job.FilesProcessed,
        error = job.Error,
        statusDetails = job.StatusDetails,
    };

    public sealed record DownloadRequest
    {
        public string Symbol { get; init; } = "";
        public string[]? Symbols { get; init; }
        public string[] Tfs { get; init; } = [];
        public int Days { get; init; } = 7;
        public DateTime? From { get; init; }
        public DateTime? To { get; init; }
        public bool KeepShards { get; set; }
    }

    public sealed record DeleteBarsRequest
    {
        public string Symbol { get; init; } = "";
        public string Timeframe { get; init; } = "";
        public DateTime? From { get; init; }
        public DateTime? To { get; init; }
        public string? Source { get; init; }
    }

    public sealed record IngestShardsRequest
    {
        public string? Symbol { get; init; }
    }
}

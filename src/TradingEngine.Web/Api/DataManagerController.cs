using Microsoft.AspNetCore.Mvc;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/data-manager")]
public sealed class DataManagerController : ControllerBase
{
    private readonly IMarketDataStore? _marketDataStore;
    private readonly DownloadJobService? _downloadJobs;
    private readonly ILogger<DataManagerController> _logger;

    public DataManagerController(
        IMarketDataStore? marketDataStore = null,
        DownloadJobService? downloadJobs = null,
        ILogger<DataManagerController>? logger = null)
    {
        _marketDataStore = marketDataStore;
        _downloadJobs = downloadJobs;
        _logger = logger!;
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
        if (string.IsNullOrWhiteSpace(req.Symbol) || req.Tfs is null or { Length: 0 })
            return BadRequest(new { error = "Symbol and at least one timeframe required." });

        if (_downloadJobs is null)
            return Problem("Download job service not registered.");

        DateTime? from = null, to = null;
        if (req.From is not null) from = DateTime.SpecifyKind(req.From.Value, DateTimeKind.Utc);
        if (req.To is not null) to = DateTime.SpecifyKind(req.To.Value, DateTimeKind.Utc);

        var job = _downloadJobs.Start(req.Symbol, req.Tfs, req.Days, from, to, keepShards: req.KeepShards);
        return Ok(new
        {
            jobId = job.Id,
            symbol = job.Symbol,
            tfs = job.Timeframes,
            status = job.Status,
        });
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
                var fi = new FileInfo(file);
                var relativePath = Path.GetRelativePath(root, file);

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
    public async Task<IActionResult> IngestShards([FromBody] IngestShardsRequest? req, CancellationToken ct)
    {
        if (_marketDataStore is null)
            return Problem("Market data store not registered.");
        if (_downloadJobs is null)
            return Problem("Download job service not registered.");

        var root = _downloadJobs.ShardsRoot;
        if (!Directory.Exists(root))
            return Ok(new { filesProcessed = 0, barsIngested = 0, errors = Array.Empty<string>() });

        var files = new List<string>();
        foreach (var file in Directory.GetFiles(root, "*.ndjson", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file);
            if (rel.StartsWith("archive", StringComparison.OrdinalIgnoreCase))
                continue;

            if (req?.Symbol is { Length: > 0 })
            {
                var fname = Path.GetFileNameWithoutExtension(file);
                if (!fname.StartsWith(req.Symbol, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            files.Add(file);
        }

        if (files.Count == 0)
            return Ok(new { filesProcessed = 0, barsIngested = 0, errors = Array.Empty<string>() });

        var ingester = new MarketDataIngester(_marketDataStore, HttpContext.RequestServices.GetService<ILogger<MarketDataIngester>>());
        var errors = new List<string>();
        int totalInserted = 0;

        foreach (var file in files)
        {
            try
            {
                var ir = await ingester.IngestFileAsync(file, "ctrader", ct);
                totalInserted += ir.BarsInserted;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                _logger.LogWarning(ex, "Ingest-shards: failed on {File}", file);
            }
        }

        _logger.LogInformation("Ingest-shards: processed {Count} files, {Bars} bars ingested, {Errors} errors",
            files.Count, totalInserted, errors.Count);

        return Ok(new
        {
            filesProcessed = files.Count,
            barsIngested = totalInserted,
            errors = errors.ToArray(),
        });
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
        error = job.Error,
    };

    public sealed record DownloadRequest
    {
        public string Symbol { get; init; } = "";
        public string[] Tfs { get; init; } = [];
        public int Days { get; init; } = 7;
        public DateTime? From { get; init; }
        public DateTime? To { get; init; }
        public bool KeepShards { get; init; }
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

using Microsoft.AspNetCore.Mvc;
using TradingEngine.Infrastructure.MarketData;
using TradingEngine.Services.Helpers;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/data-manager")]
public sealed class DataManagerController : ControllerBase
{
    private readonly IMarketDataStore? _marketDataStore;
    private readonly DownloadJobService? _downloadJobs;
    private readonly ISymbolInfoRegistry? _symbols;
    private readonly ILogger<DataManagerController> _logger;

    public DataManagerController(
        IMarketDataStore? marketDataStore = null,
        DownloadJobService? downloadJobs = null,
        ISymbolInfoRegistry? symbols = null,
        ILogger<DataManagerController>? logger = null)
    {
        _marketDataStore = marketDataStore;
        _downloadJobs = downloadJobs;
        _symbols = symbols;
        _logger = logger!;
    }

    [HttpGet("inventory")]
    public async Task<IActionResult> GetInventory(CancellationToken ct)
    {
        if (_marketDataStore is null)
            return Ok(Array.Empty<object>());

        var inventory = await _marketDataStore.GetInventoryAsync(ct);
        var items = inventory.ToList();

        var nonM1 = items.Where(i => i.Timeframe != Timeframe.M1).ToList();
        var m1Ranges = new Dictionary<string, (DateTime First, DateTime Last)>();
        foreach (var m1 in items.Where(i => i.Timeframe == Timeframe.M1))
            m1Ranges[m1.Symbol] = (m1.FirstOpenUtc, m1.LastOpenUtc);

        return Ok(items.Select(i =>
        {
            var m1Overlap = i.Timeframe != Timeframe.M1
                && m1Ranges.TryGetValue(i.Symbol, out var m1)
                && i.FirstOpenUtc <= m1.Last && i.LastOpenUtc >= m1.First;
            var spreadPips = _symbols?.TryGet(Symbol.Parse(i.Symbol), out var si) == true
                ? si.TypicalSpread / si.PipSize : (decimal?)null;
            return new
            {
                symbol = i.Symbol,
                timeframe = i.Timeframe.ToString(),
                source = i.Source,
                firstBar = i.FirstOpenUtc,
                lastBar = i.LastOpenUtc,
                barCount = i.BarCount,
                m1Overlap,
                spreadPips,
            };
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

        var job = _downloadJobs.Start(req.Symbol, req.Tfs, req.Days, from, to);
        return Ok(new
        {
            jobId = job.Id,
            symbol = job.Symbol,
            tfs = job.Timeframes,
            status = job.Status,
        });
    }

    // M4.2 — per (symbol, TF) delete range. Null from/to = whole range; null source = all sources. This
    // only touches downloaded market-data history (marketdata.db), never a run's per-RunId Bars.
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
    }

    public sealed record DeleteBarsRequest
    {
        public string Symbol { get; init; } = "";
        public string Timeframe { get; init; } = "";
        public DateTime? From { get; init; }
        public DateTime? To { get; init; }
        public string? Source { get; init; }
    }
}

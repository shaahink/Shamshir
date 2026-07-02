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
    private readonly IConfiguration? _config;
    private readonly ILogger<DataManagerController> _logger;

    public DataManagerController(
        IMarketDataStore? marketDataStore = null,
        DownloadJobService? downloadJobs = null,
        ISymbolInfoRegistry? symbols = null,
        IConfiguration? config = null,
        ILogger<DataManagerController>? logger = null)
    {
        _marketDataStore = marketDataStore;
        _downloadJobs = downloadJobs;
        _symbols = symbols;
        _config = config;
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

        var ctId = _config?.GetValue<string>("CTrader:CtId") ?? "";
        if (string.IsNullOrWhiteSpace(ctId))
            return BadRequest(new { error = "cTrader CLI not configured. Set CTrader:CtId in appsettings.json or use the seed-data endpoint to generate sample bars for testing." });

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

    [HttpPost("seed")]
    public async Task<IActionResult> SeedSampleData([FromBody] SeedRequest req, CancellationToken ct)
    {
        if (_marketDataStore is null)
            return Problem("Market data store not registered.");
        if (string.IsNullOrWhiteSpace(req.Symbol))
            return BadRequest(new { error = "Symbol required." });
        if (req.Days <= 0 || req.Days > 365)
            return BadRequest(new { error = "Days must be 1-365." });

        var sym = Symbol.Parse(req.Symbol);
        var source = "seed";

        // Check if seed data already exists for this symbol/TF — skip if so
        var existing = await _marketDataStore.GetInventoryAsync(ct);
        var h1Exists = existing.Any(e => e.Symbol == req.Symbol && e.Timeframe == Timeframe.H1 && e.Source == source);
        if (h1Exists)
            return Ok(new { bars = 0, skipped = true, message = $"Seed data already exists for {req.Symbol}. Delete existing data first to re-seed." });

        var now = DateTime.UtcNow.Date.AddDays(1);
        var start = now.AddDays(-req.Days);
        var random = new Random(42);

        // Generate H1 bars with random walk
        var h1Bars = new List<Bar>();
        decimal price = sym.Value switch
        {
            "EURUSD" => 1.0850m, "GBPUSD" => 1.2700m, "USDJPY" => 155.00m,
            "XAUUSD" => 2350m, "AUDUSD" => 0.6600m, "USDCHF" => 0.9000m,
            "USDCAD" => 1.3600m, "NZDUSD" => 0.6100m, "EURGBP" => 0.8550m,
            "EURJPY" => 168.00m, "GBPJPY" => 197.00m, "XAGUSD" => 29.00m,
            _ => 1.1000m
        };
        var baseSpread = sym.Value == "XAUUSD" ? 0.30m : sym.Value == "USDJPY" ? 0.020m : 0.00010m;

        for (var t = start; t < now; t = t.AddHours(1))
        {
            var open = price;
            var change = (decimal)((random.NextDouble() - 0.5) * 2.0 * (double)baseSpread * 15);
            price += change;
            var high = Math.Max(open, price) + (decimal)(random.NextDouble() * (double)baseSpread * 5);
            var low = Math.Min(open, price) - (decimal)(random.NextDouble() * (double)baseSpread * 5);
            var close = price;

            h1Bars.Add(new Bar(sym, Timeframe.H1, t, open, high, low, close, 100));
        }

        // Generate M1 bars interpolated from H1
        var m1Bars = new List<Bar>();
        foreach (var h1 in h1Bars)
        {
            var segments = 60;
            for (int i = 0; i < segments; i++)
            {
                var frac = (double)i / segments;
                var m1Time = h1.OpenTimeUtc.AddMinutes(i);
                var segmentOpen = i == 0 ? h1.Open : m1Bars[^1].Close;
                var drift = (decimal)((random.NextDouble() - 0.5) * (double)baseSpread * 2);
                var m1Close = segmentOpen + drift;
                var m1High = Math.Max(segmentOpen, m1Close) + (decimal)(random.NextDouble() * (double)baseSpread);
                var m1Low = Math.Min(segmentOpen, m1Close) - (decimal)(random.NextDouble() * (double)baseSpread);
                var m1Vol = i == 0 ? h1.Volume / 60.0 : 10 + random.NextDouble() * 50;

                m1Bars.Add(new Bar(sym, Timeframe.M1, m1Time,
                    segmentOpen, m1High, m1Low, m1Close, m1Vol));
            }
        }

        var totalBars = h1Bars.Count + m1Bars.Count;
        var allBars = new List<Bar>();
        allBars.AddRange(h1Bars.Select(b => b with { }));
        allBars.AddRange(m1Bars.Select(b => b with { }));

        // Chunked write to avoid memory pressure
        const int chunkSize = 5000;
        int written = 0;
        for (int i = 0; i < allBars.Count; i += chunkSize)
        {
            var chunk = allBars.Skip(i).Take(chunkSize).ToList();
            written += await _marketDataStore.WriteBarsAsync(source, chunk, ct);
        }

        _logger.LogInformation("Seeded {Bars} bars for {Symbol} (H1 + M1, {Days} days)", written, req.Symbol, req.Days);
        return Ok(new { bars = written, h1Bars = h1Bars.Count, m1Bars = m1Bars.Count });
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

    public sealed record SeedRequest
    {
        public string Symbol { get; init; } = "EURUSD";
        public int Days { get; init; } = 30;
    }
}

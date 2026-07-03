using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using TradingEngine.Domain;
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

    // Import market-data history WITHOUT cTrader: upload an NDJSON shard (MarketDataShardIo format) or a CSV
    // export (Dukascopy/MT/broker exports). Bars are deduped on (symbol, timeframe, openTime) by the store, so
    // re-importing the same window is idempotent. This is the cTrader-free path for populating tape data.
    [HttpPost("import")]
    [RequestSizeLimit(500_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 500_000_000)]
    public async Task<IActionResult> ImportFile(
        IFormFile? file,
        [FromForm] string? source,
        [FromForm] string? symbol,
        [FromForm] string? timeframe,
        CancellationToken ct)
    {
        if (_marketDataStore is null)
            return Problem("Market data store not registered.");
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file uploaded." });

        var src = string.IsNullOrWhiteSpace(source) ? "import" : source.Trim();
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();

        var tmpDir = Path.Combine(Path.GetTempPath(), "shamshir-import");
        Directory.CreateDirectory(tmpDir);
        var tmp = Path.Combine(tmpDir, Guid.NewGuid().ToString("N") + ext);
        try
        {
            await using (var fs = System.IO.File.Create(tmp))
                await file.CopyToAsync(fs, ct);

            if (ext == ".csv")
            {
                var (bars, lines, errors) = ParseCsv(tmp, symbol, timeframe, out var parseError);
                if (parseError is not null)
                    return BadRequest(new { error = parseError });
                var inserted = bars.Count > 0 ? await _marketDataStore.WriteBarsAsync(src, bars, ct) : 0;
                _logger.LogInformation("Import {File}: {Lines} CSV rows -> {Inserted} new bars, {Errors} errors", file.FileName, lines, inserted, errors);
                return Ok(new { fileName = file.FileName, format = "csv", linesRead = lines, barsInserted = inserted, parseErrors = errors, source = src });
            }
            else
            {
                // NDJSON (default): reuse the tested, streaming ingester.
                var ingester = new MarketDataIngester(_marketDataStore, null);
                var r = await ingester.IngestFileAsync(tmp, src, ct);
                _logger.LogInformation("Import {File}: {Lines} NDJSON lines -> {Inserted} new bars, {Errors} errors", file.FileName, r.LinesRead, r.BarsInserted, r.ParseErrors);
                return Ok(new { fileName = file.FileName, format = "ndjson", linesRead = r.LinesRead, barsInserted = r.BarsInserted, parseErrors = r.ParseErrors, source = src });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Import failed for {File}", file.FileName);
            return StatusCode(500, new { error = ex.Message });
        }
        finally
        {
            try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { /* best-effort cleanup */ }
        }
    }

    // Flexible CSV → bars. Header row required (case-insensitive). Recognizes common column names:
    // time/date/timestamp/opentime[utc] · open/o · high/h · low/l · close/c · volume/vol/v · symbol · timeframe/tf/period.
    // symbol/timeframe fall back to the form fields when absent from the file. Malformed rows are skipped and counted.
    private static (List<Bar> bars, int lines, int errors) ParseCsv(
        string path, string? formSymbol, string? formTimeframe, out string? fatalError)
    {
        fatalError = null;
        var bars = new List<Bar>();
        int lines = 0, errors = 0;

        using var reader = new StreamReader(path);
        var headerLine = reader.ReadLine();
        if (headerLine is null) { fatalError = "Empty file."; return (bars, 0, 0); }

        var headers = SplitCsv(headerLine);
        int Idx(params string[] names)
        {
            for (var i = 0; i < headers.Length; i++)
                if (names.Contains(headers[i].Trim().ToLowerInvariant())) return i;
            return -1;
        }

        int tIdx = Idx("opentimeutc", "opentime", "timestamp", "time", "date", "datetime");
        int oIdx = Idx("open", "o"), hIdx = Idx("high", "h"), lIdx = Idx("low", "l"), cIdx = Idx("close", "c");
        int vIdx = Idx("volume", "vol", "v");
        int symIdx = Idx("symbol", "sym"), tfIdx = Idx("timeframe", "tf", "period");

        if (tIdx < 0 || oIdx < 0 || hIdx < 0 || lIdx < 0 || cIdx < 0)
        {
            fatalError = "CSV must have a header with time, open, high, low, close columns.";
            return (bars, 0, 0);
        }

        var symFallback = formSymbol?.Trim();
        var tfFallback = formTimeframe?.Trim();

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            lines++;
            var f = SplitCsv(line);
            try
            {
                var symStr = symIdx >= 0 && symIdx < f.Length ? f[symIdx].Trim() : symFallback;
                var tfStr = tfIdx >= 0 && tfIdx < f.Length ? f[tfIdx].Trim() : tfFallback;
                if (string.IsNullOrEmpty(symStr) || string.IsNullOrEmpty(tfStr)) { errors++; continue; }
                if (!Enum.TryParse<Timeframe>(tfStr, ignoreCase: true, out var tf)) { errors++; continue; }
                if (!TryParseTime(f[tIdx].Trim(), out var t)) { errors++; continue; }

                var o = decimal.Parse(f[oIdx].Trim(), CultureInfo.InvariantCulture);
                var h = decimal.Parse(f[hIdx].Trim(), CultureInfo.InvariantCulture);
                var l = decimal.Parse(f[lIdx].Trim(), CultureInfo.InvariantCulture);
                var c = decimal.Parse(f[cIdx].Trim(), CultureInfo.InvariantCulture);
                double vol = vIdx >= 0 && vIdx < f.Length && double.TryParse(f[vIdx].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var pv) ? pv : 0d;

                bars.Add(new Bar(Symbol.Parse(symStr), tf, DateTime.SpecifyKind(t, DateTimeKind.Utc), o, h, l, c, vol));
            }
            catch { errors++; }
        }
        return (bars, lines, errors);
    }

    private static bool TryParseTime(string s, out DateTime t) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)
        || DateTime.TryParseExact(s, ["yyyy-MM-dd HH:mm:ss", "yyyy.MM.dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy.MM.dd HH:mm:ss"],
               CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t);

    private static string[] SplitCsv(string line)
    {
        // OHLCV/symbol/time data has no embedded commas, so a plain split is sufficient and fast.
        // Accept comma, semicolon, or tab as the delimiter (broker exports vary).
        var delim = line.Contains('\t') ? '\t' : line.Contains(';') && !line.Contains(',') ? ';' : ',';
        return line.Split(delim);
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

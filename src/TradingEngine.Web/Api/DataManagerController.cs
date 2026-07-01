using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TradingEngine.CTraderRunner;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Web.Api;

[ApiController]
[Route("api/data-manager")]
public sealed class DataManagerController : ControllerBase
{
    private readonly IMarketDataStore? _marketDataStore;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DataManagerController> _logger;

    public DataManagerController(
        IMarketDataStore? marketDataStore = null,
        IConfiguration? configuration = null,
        ILogger<DataManagerController>? logger = null)
    {
        _marketDataStore = marketDataStore;
        _configuration = configuration!;
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
    public async Task<IActionResult> StartDownload([FromBody] DownloadRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Symbol) || req.Tfs is null or { Length: 0 })
            return BadRequest(new { error = "Symbol and at least one timeframe required." });

        var shardsDir = Path.Combine(
            Path.GetTempPath(), "shamshir-download", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(shardsDir);

        try
        {
            var ctId = _configuration.GetValue<string>("CTrader:CtId") ?? "";
            var pwdFile = _configuration.GetValue<string>("CTrader:PwdFile") ?? "";
            var account = _configuration.GetValue<string>("CTrader:Account") ?? "";

            if (string.IsNullOrWhiteSpace(ctId) || string.IsNullOrWhiteSpace(pwdFile))
                return BadRequest(new { error = "cTrader credentials not configured." });

            var algoPath = ResolveAlgo();
            if (!System.IO.File.Exists(algoPath))
                return BadRequest(new { error = "cBot algo not found. Build TradingEngine.Adapters.CTrader first." });

            var end = DateTime.UtcNow.Date;
            var start = end.AddDays(-(req.Days));

            var periodsStr = string.Join(",", req.Tfs);
            var cliReq = new BacktestCliRequest
            {
                AlgoPath = algoPath,
                Symbol = req.Symbol,
                Period = req.Tfs[0],
                Start = start,
                End = end,
                CtId = ctId,
                PwdFile = pwdFile,
                Account = account,
                DataPort = 15562,
                CommandPort = 15563,
                Balance = 100_000m,
                FullAccess = true,
                DataMode = "m1",
                ReportDir = shardsDir,
                Record = true,
                Periods = [periodsStr],
            };

            var result = await BacktestCli.InvokeAsync(cliReq, ct);

            if (_marketDataStore is not null)
            {
                var ingester = new MarketDataIngester(_marketDataStore);
                int total = 0;
                foreach (var shard in Directory.GetFiles(shardsDir, "*.ndjson"))
                {
                    var ir = await ingester.IngestFileAsync(shard, "ctrader", ct);
                    total += ir.BarsInserted;
                }

                return Ok(new
                {
                    symbol = req.Symbol,
                    tfs = req.Tfs,
                    barsRecorded = total,
                    cliExitCode = result.ExitCode,
                });
            }

            return Ok(new { symbol = req.Symbol, tfs = req.Tfs, cliExitCode = result.ExitCode });
        }
        finally
        {
            try { if (Directory.Exists(shardsDir)) Directory.Delete(shardsDir, true); } catch { }
        }
    }

    private string ResolveAlgo()
    {
        var configured = _configuration["CTrader:AlgoPath"];
        if (!string.IsNullOrEmpty(configured) && System.IO.File.Exists(configured))
            return configured;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Debug", "net6.0", "src.algo")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Release", "net6.0", "src.algo")),
        };

        return candidates.FirstOrDefault(System.IO.File.Exists)
            ?? throw new FileNotFoundException("src.algo not found. Build TradingEngine.Adapters.CTrader first.");
    }

    public sealed record DownloadRequest
    {
        public string Symbol { get; init; } = "";
        public string[] Tfs { get; init; } = [];
        public int Days { get; init; } = 7;
    }
}

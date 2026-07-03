using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TradingEngine.CTraderRunner;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Web.Api;

public sealed class DownloadJobService
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DownloadJobService> _logger;

    public DownloadJobService(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<DownloadJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;

        var dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data"));
        ShardsRoot = configuration.GetValue<string>("MarketData:ShardsPath")
            ?? Path.Combine(dataDir, "shards");
        Directory.CreateDirectory(ShardsRoot);
    }

    public string ShardsRoot { get; }

    public DownloadJob Start(string symbol, string[] tfs, int days, DateTime? from, DateTime? to, bool keepShards = false)
    {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        var job = new DownloadJob
        {
            Id = jobId,
            Symbol = symbol,
            Timeframes = tfs,
            Days = days,
            From = from,
            To = to,
            Status = "queued",
            CreatedAtUtc = DateTime.UtcNow,
            KeepShards = keepShards,
        };
        _jobs[jobId] = job;

        _ = Task.Run(() => ExecuteAsync(job));
        return job;
    }

    public DownloadJob? Get(string jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job : null;

    public IReadOnlyList<DownloadJob> List() =>
        [.. _jobs.Values.OrderByDescending(j => j.CreatedAtUtc)];

    private async Task ExecuteAsync(DownloadJob job)
    {
        job.Status = "running";
        job.StartedAtUtc = DateTime.UtcNow;

        var shardsDir = Path.Combine(ShardsRoot, job.Id);
        try
        {
            Directory.CreateDirectory(shardsDir);

            var ctId = _configuration.GetValue<string>("CTrader:CtId") ?? "";
            var pwdFile = _configuration.GetValue<string>("CTrader:PwdFile") ?? "";
            var account = _configuration.GetValue<string>("CTrader:Account") ?? "";

            if (string.IsNullOrWhiteSpace(ctId) || string.IsNullOrWhiteSpace(pwdFile))
            {
                job.Status = "failed";
                job.Error = "cTrader credentials not configured.";
                return;
            }

            var algoPath = ResolveAlgo();
            if (!File.Exists(algoPath))
            {
                job.Status = "failed";
                job.Error = "cBot algo not found. Build TradingEngine.Adapters.CTrader first.";
                return;
            }

            var end = job.To ?? DateTime.UtcNow.Date;
            var start = job.From ?? end.AddDays(-job.Days);

            var periodsStr = string.Join(",", job.Timeframes);

            job.Status = "recording";
            var cliReq = new BacktestCliRequest
            {
                AlgoPath = algoPath,
                Symbol = job.Symbol,
                Period = job.Timeframes[0],
                Start = start,
                End = end,
                CtId = ctId,
                PwdFile = pwdFile,
                Account = account,
                Balance = 100_000m,
                FullAccess = true,
                DataMode = "m1",
                ReportDir = shardsDir,
                Record = true,
                Periods = [periodsStr],
                DataPort = 15562,
                CommandPort = 15563,
            };

            _logger.LogInformation("Download job {JobId}: starting cTrader CLI for {Symbol} {Tfs}", job.Id, job.Symbol, periodsStr);
            var result = await BacktestCli.InvokeAsync(cliReq, CancellationToken.None);

            if (result.ExitCode != 0)
            {
                job.Status = "failed";
                job.Error = $"cTrader CLI exited with code {result.ExitCode}";
                _logger.LogWarning("Download job {JobId}: cTrader CLI failed with exit code {ExitCode}", job.Id, result.ExitCode);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IMarketDataStore>();

            job.Status = "ingesting";
            var ingester = new MarketDataIngester(store, scope.ServiceProvider.GetService<ILogger<MarketDataIngester>>());
            int total = 0;
            foreach (var shard in Directory.GetFiles(shardsDir, "*.ndjson"))
            {
                var ir = await ingester.IngestFileAsync(shard, "ctrader", CancellationToken.None);
                total += ir.BarsInserted;
            }

            job.BarsRecorded = total;
            job.Status = "done";
            job.CompletedAtUtc = DateTime.UtcNow;
            _logger.LogInformation("Download job {JobId}: complete — {Bars} bars ingested", job.Id, total);
        }
        catch (Exception ex)
        {
            job.Status = "failed";
            job.Error = ex.Message;
            _logger.LogError(ex, "Download job {JobId}: failed", job.Id);
        }
        finally
        {
            if (job.Status == "done")
            {
                if (job.KeepShards)
                {
                    var archiveDir = Path.Combine(ShardsRoot, "archive");
                    try
                    {
                        Directory.CreateDirectory(archiveDir);
                        var archivedDest = Path.Combine(archiveDir, job.Id);
                        if (Directory.Exists(archivedDest)) Directory.Delete(archivedDest, true);
                        Directory.Move(shardsDir, archivedDest);
                        _logger.LogInformation("Download job {JobId}: shards archived to {Dir}", job.Id, archivedDest);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Download job {JobId}: could not archive shards from {Dir}", job.Id, shardsDir);
                    }
                }
                else
                {
                    try { if (Directory.Exists(shardsDir)) Directory.Delete(shardsDir, true); }
                    catch { _logger.LogWarning("Download job {JobId}: could not clean up shards dir {Dir}", job.Id, shardsDir); }
                }
            }
            else if (job.Status == "failed")
            {
                try { if (Directory.Exists(shardsDir)) Directory.Delete(shardsDir, true); }
                catch { _logger.LogWarning("Download job {JobId}: could not clean up failed shards dir {Dir}", job.Id, shardsDir); }
            }
            else
            {
                _logger.LogWarning("Download job {JobId}: shards retained at {Dir} (status={Status})", job.Id, shardsDir, job.Status);
            }
        }
    }

    private string ResolveAlgo()
    {
        var configured = _configuration["CTrader:AlgoPath"];
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Debug", "net6.0", "src.algo")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Release", "net6.0", "src.algo")),
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("src.algo not found. Build TradingEngine.Adapters.CTrader first.");
    }
}

public sealed class DownloadJob
{
    public string Id { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string[] Timeframes { get; set; } = [];
    public int Days { get; set; } = 7;
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string Status { get; set; } = "queued";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int BarsRecorded { get; set; }
    public string? Error { get; set; }
    public bool KeepShards { get; set; }
}

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TradingEngine.CTraderRunner;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.MarketData;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Api;

public sealed class DownloadJobService
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly CTraderConnectionOptions _ctraderOptions;
    private readonly CTraderProcessOwner _owner;
    private readonly ILogger<DownloadJobService> _logger;

    public DownloadJobService(IServiceScopeFactory scopeFactory, IConfiguration configuration,
        IOptions<CTraderConnectionOptions> ctraderOptions, CTraderProcessOwner owner,
        ILogger<DownloadJobService> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _ctraderOptions = ctraderOptions.Value;
        _owner = owner;
        _logger = logger;

        var dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data"));
        ShardsRoot = configuration.GetValue<string>("MarketData:ShardsPath")
            ?? Path.Combine(dataDir, "shards");
        Directory.CreateDirectory(ShardsRoot);
    }

    public string ShardsRoot { get; }

    public DownloadJob Start(string symbol, string[] tfs, int days, DateTime? from, DateTime? to, bool keepShards = false, int timeoutSeconds = 0)
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
            TimeoutSeconds = timeoutSeconds,
        };
        _jobs[jobId] = job;

        _ = Task.Run(() => ExecuteAsync(job));
        return job;
    }

    public DownloadJob StartIngest(string? symbol = null)
    {
        var jobId = Guid.NewGuid().ToString("N")[..8];
        var job = new DownloadJob
        {
            Id = jobId,
            Symbol = symbol ?? "all",
            Status = "ingesting",
            CreatedAtUtc = DateTime.UtcNow,
        };
        _jobs[jobId] = job;

        _ = Task.Run(() => ExecuteIngestAsync(job, symbol));
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

            var ctId = _ctraderOptions.CtId;
            var pwdFile = _ctraderOptions.PwdFile;
            var account = _ctraderOptions.Account;

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
            // X4: dynamic ports (no more hardcoded 15562/15563 collision when downloads run in parallel
            // or alongside a backtest / another worktree). Ports are allocated per invocation.
            var (dataPort, commandPort) = CTraderProcessOwner.AllocatePorts();
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
                Symbols = [job.Symbol],
                Periods = [periodsStr],
                DataPort = dataPort,
                CommandPort = commandPort,
                TimeoutSeconds = job.TimeoutSeconds,
            };

            _logger.LogInformation("Download job {JobId}: starting cTrader CLI for {Symbol} {Tfs} on ports {DataPort}/{CommandPort}",
                job.Id, job.Symbol, periodsStr, dataPort, commandPort);

            // X4: the cTrader invocation runs under the shared owner lane (bounded parallelism, shared
            // with backtests). We register the PID so the owner can tree-kill only what it launched, and
            // run an idle-watchdog: ctrader-cli is known to HANG on exit after a complete record, so once
            // the shard stops growing we reap it rather than wait out the full timeout. The lane is
            // released the moment the CLI returns — ingest (below) touches only the DB.
            BacktestCliResult result;
            int? ctraderPid = null;
            using var recordCts = new CancellationTokenSource();
            var watchdog = RunRecordWatchdogAsync(job.Id, shardsDir, recordCts);
            try
            {
                using (await _owner.AcquireAsync(CancellationToken.None))
                {
                    result = await BacktestCli.InvokeAsync(cliReq, recordCts.Token,
                        onStarted: pid => { ctraderPid = pid; _owner.Register(pid, $"download:{job.Id}"); });
                }
            }
            catch (OperationCanceledException)
            {
                // Watchdog reaped a hung CLI after the record went idle — its data is on disk, so this is
                // a normal completion, not a failure.
                result = new BacktestCliResult { ExitCode = 0, StdErr = "record complete; CLI reaped after idle" };
                _logger.LogInformation("Download job {JobId}: CLI reaped after idle record", job.Id);
            }
            finally
            {
                recordCts.Cancel();
                if (ctraderPid is int startedPid) _owner.Unregister(startedPid);
            }

            // X4: ingest whatever was recorded, REGARDLESS of exit code. Recording writes bars to disk as
            // it goes and finishes well before the hang; a non-zero exit (timeout/reap) must not throw
            // that data away. Only fail if nothing at all was recorded.
            var shardFiles = Directory.Exists(shardsDir)
                ? Directory.GetFiles(shardsDir, "*.ndjson")
                : [];

            if (shardFiles.Length == 0)
            {
                job.Status = "failed";
                job.Error = result.ExitCode != 0
                    ? $"cTrader CLI exited with code {result.ExitCode} and recorded no data"
                    : "cTrader CLI produced no shards";
                _logger.LogWarning("Download job {JobId}: no shards recorded (exit {ExitCode})", job.Id, result.ExitCode);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IMarketDataStore>();

            job.Status = "ingesting";
            var ingester = new MarketDataIngester(store, scope.ServiceProvider.GetService<ILogger<MarketDataIngester>>());
            int total = 0;
            foreach (var shard in shardFiles)
            {
                var ir = await ingester.IngestFileAsync(shard, "ctrader", CancellationToken.None);
                total += ir.BarsInserted;
            }

            job.BarsRecorded = total;
            job.Status = "done";
            job.CompletedAtUtc = DateTime.UtcNow;
            _logger.LogInformation("Download job {JobId}: complete — {Bars} bars ingested (exit {ExitCode})",
                job.Id, total, result.ExitCode);
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

    /// <summary>
    /// ctrader-cli hangs on exit after a complete record. Once at least one shard exists and none has
    /// grown for <c>IdleReapSeconds</c>, the record is done — cancel the record token so the CLI is
    /// tree-killed and the job proceeds straight to ingest instead of waiting out the full timeout. It
    /// never reaps before a shard appears, so a slow-to-connect record is not cut short.
    /// </summary>
    private async Task RunRecordWatchdogAsync(string jobId, string shardsDir, CancellationTokenSource recordCts)
    {
        const int idleReapSeconds = 45;
        try
        {
            while (!recordCts.IsCancellationRequested)
            {
                await Task.Delay(5000, recordCts.Token);
                if (!Directory.Exists(shardsDir)) continue;
                var files = Directory.GetFiles(shardsDir, "*.ndjson");
                if (files.Length == 0) continue;
                var lastWrite = files.Max(File.GetLastWriteTimeUtc);
                if (DateTime.UtcNow - lastWrite > TimeSpan.FromSeconds(idleReapSeconds))
                {
                    _logger.LogInformation("Download job {JobId}: record idle {Idle}s — reaping CLI", jobId, idleReapSeconds);
                    recordCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* record finished; token cancelled by the caller */ }
    }

    private async Task ExecuteIngestAsync(DownloadJob job, string? symbol)
    {
        try
        {
            var root = ShardsRoot;
            if (!Directory.Exists(root))
            {
                job.Status = "done";
                job.BarsRecorded = 0;
                return;
            }

            var files = new List<string>();
            foreach (var file in Directory.GetFiles(root, "*.ndjson", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file);
                if (rel.StartsWith("archive", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(symbol))
                {
                    var fname = Path.GetFileNameWithoutExtension(file);
                    if (!fname.StartsWith(symbol, StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                files.Add(file);
            }

            if (files.Count == 0)
            {
                job.Status = "done";
                job.BarsRecorded = 0;
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IMarketDataStore>();
            var ingester = new MarketDataIngester(store, scope.ServiceProvider.GetService<ILogger<MarketDataIngester>>());

            job.FilesTotal = files.Count;
            var progress = new Progress<IngestProgress>(p =>
            {
                job.FilesProcessed = p.FilesProcessed;
                job.BarsRecorded = p.BarsInserted;
                job.LinesProcessed = p.LinesRead;
                job.StatusDetails = p.FileName ?? "";
            });

            foreach (var file in files)
            {
                var ir = await ingester.IngestFileAsync(file, "ctrader", CancellationToken.None, progress);
                job.BarsRecorded = ir.BarsInserted;

                var archiveDir = Path.Combine(root, "archive");
                Directory.CreateDirectory(archiveDir);
                var dest = Path.Combine(archiveDir, Path.GetFileName(file));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(file, dest);
            }

            job.Status = "done";
            job.CompletedAtUtc = DateTime.UtcNow;
            _logger.LogInformation("Ingest job {JobId}: complete — {Files} files, {Bars} bars",
                job.Id, files.Count, job.BarsRecorded);
        }
        catch (Exception ex)
        {
            job.Status = "failed";
            job.Error = ex.Message;
            _logger.LogError(ex, "Ingest job {JobId}: failed", job.Id);
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
    public int LinesProcessed { get; set; }
    public int FilesTotal { get; set; }
    public int FilesProcessed { get; set; }
    public string? Error { get; set; }
    public string? StatusDetails { get; set; }
    public bool KeepShards { get; set; }
    public int TimeoutSeconds { get; set; }
}

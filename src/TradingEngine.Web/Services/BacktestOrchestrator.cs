using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TradingEngine.CTraderRunner;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.Events;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Risk;
using TradingEngine.Risk.Filters;
using TradingEngine.Services;

namespace TradingEngine.Web.Services;

public sealed class BacktestOrchestrator : IBacktestCommandService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BacktestOrchestrator> _logger;
    private readonly BacktestProgressStore _progressStore;
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, BacktestRunState> _runs = new();

    private sealed record TradeStats(decimal NetProfit, decimal MaxDrawdownPct, int TotalTrades, int WinningTrades, double WinRatePct);

    public sealed record BacktestRunState
    {
        public required string RunId { get; init; }
        public DateTime StartedAt { get; init; } = DateTime.UtcNow;
        public string Status { get; set; } = "starting";
        public BacktestResult? Result { get; set; }
        public string? Error { get; set; }
        public string Symbol { get; init; } = "";
        public string Period { get; init; } = "";
        public ConcurrentQueue<string> LogLines { get; init; } = new();
        public CancellationTokenSource? CancellationSource { get; set; }
        public Task? RunTask { get; set; }
        public IReadOnlyList<string> GetLogs() => LogLines.ToArray();
    }

    public BacktestOrchestrator(
        IServiceScopeFactory scopeFactory,
        BacktestProgressStore progressStore,
        IConfiguration configuration,
        ILogger<BacktestOrchestrator> logger)
    {
        _scopeFactory = scopeFactory;
        _progressStore = progressStore;
        _configuration = configuration;
        _logger = logger;
    }

    private void PushProgress(string runId, string line)
    {
        var json = JsonSerializer.Serialize(new { line });
        _progressStore.GetWriter(runId).TryWrite(json);
    }

    private void PushProgressEvent(string runId, string eventType, string message)
    {
        var json = JsonSerializer.Serialize(new { eventType, message });
        _progressStore.GetWriter(runId).TryWrite(json);
    }

    private void PushProgressAndLog(string runId, ConcurrentQueue<string> logQueue, string line)
    {
        logQueue.Enqueue(line);
        PushProgress(runId, line);
    }

    private void EnqueueLog(string runId, ConcurrentQueue<string> queue, string msg)
    {
        queue.Enqueue(msg);
        PushProgress(runId, msg);
    }

    private static Timeframe ParseTimeframe(string period) => period.ToUpperInvariant() switch
    {
        "M1"  => Timeframe.M1,
        "M5"  => Timeframe.M5,
        "M15" => Timeframe.M15,
        "M30" => Timeframe.M30,
        "H1"  => Timeframe.H1,
        "H4"  => Timeframe.H4,
        "D1"  => Timeframe.D1,
        _     => Timeframe.H1,
    };

    public BacktestRunState Start(BacktestConfig cfg)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        cfg = cfg with { RunId = runId };
        var state = new BacktestRunState { RunId = runId, Symbol = cfg.Symbol, Period = cfg.Period };
        _runs[runId] = state;
        state.CancellationSource = new CancellationTokenSource();

        EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Starting backtest {runId}...");

        state.RunTask = RunAsync(runId, cfg, state.CancellationSource.Token);

        return state;
    }

    public BacktestRunState? GetState(string runId) =>
        _runs.TryGetValue(runId, out var state) ? state : null;

    public IReadOnlyList<BacktestRunState> GetAll() => _runs.Values.ToList();

    public async Task<string> StartAsync(BacktestConfig cfg, CancellationToken ct)
    {
        var state = Start(cfg);
        await Task.CompletedTask;
        return state.RunId;
    }

    public void Cancel(string runId)
    {
        if (_runs.TryGetValue(runId, out var state))
        {
            state.Status = "cancelled";
            state.CancellationSource?.Cancel();
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var (_, state) in _runs)
            state.CancellationSource?.Cancel();

        var tasks = _runs.Values
            .Select(s => s.RunTask)
            .Where(t => t is not null)
            .ToArray();

        if (tasks.Length > 0)
            await Task.WhenAll(tasks!);
    }

    private async Task RunAsync(string runId, BacktestConfig cfg, CancellationToken ct)
    {
        var state = _runs[runId];
        var startedAt = state.StartedAt;

        await WriteStartRecordAsync(runId, cfg, startedAt);

        try
        {
            state.Status = "running";
            EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Starting backtest {runId}...");

            BacktestResult result;

            var useCtader = _configuration.GetValue<bool>("CTrader:UseForBacktest");
            if (useCtader)
            {
                EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Running via in-process cTrader engine...");
                result = await RunEngineNetMqAsync(runId, cfg, state.LogLines, ct);
            }
            else
            {
                EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Running engine replay...");
                result = await RunEngineReplayAsync(runId, cfg, state.LogLines);
            }

            var tradeStats = await GetTradeStatsAsync(runId, cfg.Balance);

            result = result with
            {
                NetProfit      = tradeStats.NetProfit,
                MaxDrawdownPct = tradeStats.MaxDrawdownPct,
                TotalTrades    = tradeStats.TotalTrades,
                WinningTrades  = tradeStats.WinningTrades,
                WinRatePct     = tradeStats.WinRatePct,
            };

            state.Result = result;
            state.Status = result.Success ? "completed" : "failed";
            state.Error = result.ErrorMessage;

            EnqueueLog(runId, state.LogLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] Done. Trades={result.TotalTrades} PnL={result.NetProfit:N2} DD={result.MaxDrawdownPct:P1}");

            await WriteEndRecordAsync(runId, cfg, startedAt, result, tradeStats);
        }
        catch (Exception ex)
        {
            state.Status = "failed";
            state.Error = ex.Message;
            EnqueueLog(runId, state.LogLines, $"[{DateTime.UtcNow:HH:mm:ss}] Error: {ex.Message}");
            _logger.LogError(ex, "Backtest {RunId} failed", runId);

            await WriteEndRecordAsync(runId, cfg, startedAt,
                new BacktestResult { RunId = runId, ExitCode = 1, ErrorMessage = ex.Message },
                new(0, 0, 0, 0, 0));
        }
        finally
        {
            var doneJson = JsonSerializer.Serialize(
                new { done = true, status = state.Status, error = state.Error });
            _progressStore.GetWriter(runId).TryWrite(doneJson);
            _progressStore.Complete(runId);
        }
    }

    private async Task WriteStartRecordAsync(string runId, BacktestConfig cfg, DateTime startedAt)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
            var summary = new BacktestRunSummary(
                runId, startedAt, DateTime.MinValue,
                cfg.Symbol, cfg.Period, cfg.Start, cfg.End,
                cfg.Balance, "", "{}",
                0, 0, 0, 0, 0, -1, null);
            await repo.SaveAsync(summary, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write start record for {RunId}", runId);
        }
    }

    private async Task WriteEndRecordAsync(
        string runId, BacktestConfig cfg, DateTime startedAt,
        BacktestResult result, TradeStats stats)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBacktestRunRepository>();
            var summary = new BacktestRunSummary(
                runId, startedAt, DateTime.UtcNow,
                cfg.Symbol, cfg.Period, cfg.Start, cfg.End,
                cfg.Balance, result.AlgoHash, "{}",
                stats.NetProfit, stats.MaxDrawdownPct,
                stats.TotalTrades, stats.WinningTrades, stats.WinRatePct,
                result.ExitCode, result.ErrorMessage);
            await repo.UpdateAsync(summary, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write end record for {RunId}", runId);
        }
    }

    private async Task<BacktestResult> RunEngineReplayAsync(
        string runId, BacktestConfig cfg, ConcurrentQueue<string> logLines)
    {
        var symbol    = Symbol.Parse(cfg.Symbol);
        var timeframe = ParseTimeframe(cfg.Period);
        var from      = cfg.Start;
        var to        = cfg.End;

        var dbPath = _configuration.GetValue<string>("Persistence:DbPath")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "data", "trading.db"));

        using var scope = _scopeFactory.CreateScope();
        var barRepo = scope.ServiceProvider.GetRequiredService<IBarRepository>();

        var solutionRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        var progressCallback = new Progress<BacktestProgressEvent>(evt =>
        {
            PushProgressEvent(runId, evt.EventType, evt.Message);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));

        var innerHost = EngineHostFactory.Create(new EngineHostOptions
        {
            RunId = runId,
            Mode = EngineMode.Backtest,
            AdapterFactory = sp => new BacktestReplayAdapter(barRepo, symbol, timeframe, from, to,
                cfg.Balance, sp.GetRequiredService<ILogger<BacktestReplayAdapter>>()),
            DbPath = dbPath,
            SolutionRoot = solutionRoot,
            SymbolNames = new[] { cfg.Symbol },
            Progress = progressCallback,
            MinLogLevel = LogLevel.Warning,
        });
        EngineHostFactory.WireEventHandlers(innerHost);
        EngineHostFactory.WireRiskRules(innerHost);

        await innerHost.StartAsync(cts.Token);
        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Engine started. Replaying bars...");

        var adapter = innerHost.Services.GetRequiredService<IBrokerAdapter>();
        await adapter.BarStream.Completion;

        var barCount = (adapter as BacktestReplayAdapter)?.BarCount ?? 0;
        if (barCount == 0)
        {
            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] No bars found for {cfg.Symbol}/{cfg.Period} in {cfg.Start:yyyy-MM-dd}–{cfg.End:yyyy-MM-dd}. Run scripts/seed-bars.ps1 to seed data.");
            await innerHost.StopAsync(CancellationToken.None);
            innerHost.Dispose();
            return new BacktestResult { RunId = runId, ExitCode = 1, AlgoHash = "",
                ErrorMessage = $"No bars found for {cfg.Symbol}/{cfg.Period}." };
        }

        await Task.Delay(5_000, cts.Token);

        var barHandler = innerHost.Services.GetRequiredService<BarEvaluationHandler>();
        await barHandler.FlushRemainingAsync();

        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Engine replay complete.");
        await innerHost.StopAsync(CancellationToken.None);
        innerHost.Dispose();

        return new BacktestResult { RunId = runId, ExitCode = 0, AlgoHash = "" };
    }

    private async Task<BacktestResult> RunEngineNetMqAsync(
        string runId, BacktestConfig cfg, ConcurrentQueue<string> logLines, CancellationToken ct)
    {
        var ctid = _configuration["CTrader:CtId"];
        var pwdFile = _configuration["CTrader:PwdFile"];
        var account = _configuration["CTrader:Account"];
        if (string.IsNullOrWhiteSpace(ctid) || string.IsNullOrWhiteSpace(pwdFile) || string.IsNullOrWhiteSpace(account))
        {
            EnqueueLog(runId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] CTrader credentials not configured");
            return new BacktestResult { RunId = runId, ExitCode = 1, AlgoHash = "",
                ErrorMessage = "CTrader credentials not configured." };
        }

        var algoPath = ResolveAlgoPath();
        var algoHash = ComputeAlgoHash(algoPath);

        var symbol    = Symbol.Parse(cfg.Symbol);
        var timeframe = ParseTimeframe(cfg.Period);

        var (dataPort, commandPort) = AllocatePorts();

        var dbPath = _configuration.GetValue<string>("Persistence:DbPath")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "data", "trading.db"));

        var solutionRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct,
            new CancellationTokenSource(TimeSpan.FromMinutes(30)).Token);

        var progressCallback = new Progress<BacktestProgressEvent>(evt =>
        {
            PushProgressEvent(runId, evt.EventType, evt.Message);
            if (evt.EventType is "EXEC" or "REJECTED" or "NETMQ_CONNECTED" or "NETMQ_SENT" or "NETMQ_DROPPED" or "CBOT")
                PushProgressAndLog(runId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] {evt.EventType} {evt.Message}");
        });

        var symbolInfo = new SymbolInfo(symbol, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

        var innerHost = EngineHostFactory.Create(new EngineHostOptions
        {
            RunId = runId,
            Mode = EngineMode.Backtest,
            AdapterFactory = sp =>
            {
                var adapter = new NetMQBrokerAdapter(
                    $"tcp://127.0.0.1:{dataPort}",
                    $"tcp://*:{commandPort}",
                    sp.GetRequiredService<ILogger<NetMQBrokerAdapter>>());
                adapter.OnStatusChange = (type, msg) =>
                {
                    PushProgressEvent(runId, type, msg);
                    PushProgressAndLog(runId, logLines, $"[{DateTime.UtcNow:HH:mm:ss}] {type} {msg}");
                };
                return adapter;
            },
            DbPath = dbPath,
            SolutionRoot = solutionRoot,
            SymbolNames = new[] { cfg.Symbol },
            Progress = progressCallback,
            MinLogLevel = LogLevel.Information,
        });
        EngineHostFactory.WireEventHandlers(innerHost);
        EngineHostFactory.WireRiskRules(innerHost);

        try
        {
            await innerHost.StartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            innerHost.Dispose();
            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] Engine start failed: {ex.Message}");
            return new BacktestResult { RunId = runId, ExitCode = 1, AlgoHash = algoHash,
                ErrorMessage = $"Engine start failed: {ex.Message}" };
        }

        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Engine started (in-process NetMQ). Ports={dataPort}/{commandPort}");

        var resultsDir = Path.Combine(Path.GetTempPath(), "shamshir-backtest", runId);
        Directory.CreateDirectory(resultsDir);

        var cli = new CTraderCli();
        var args = new[]
        {
            $"--start={cfg.Start:dd/MM/yyyy}", $"--end={cfg.End:dd/MM/yyyy}",
            $"--symbol={cfg.Symbol}", $"--period={cfg.Period}",
            $"--balance={cfg.Balance}", $"--commission={cfg.CommissionPerMillion}",
            $"--spread={cfg.SpreadPips}", $"--data-mode={cfg.DataMode}",
            $"--ctid={ctid}", $"--pwd-file={pwdFile}", $"--account={account}",
            $"--DataPort={dataPort}", $"--CommandPort={commandPort}",
            $"--SymbolString={string.Join(",", cfg.Symbols)}",
            $"--Periods={string.Join(",", cfg.Periods)}",
            "--full-access",
        };

        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] Launching ctrader-cli...");
        CTraderResult cliResult;
        try
        {
            cliResult = await cli.BacktestAsync(algoPath, args, cts.Token);
        }
        finally
        {
            await innerHost.StopAsync(CancellationToken.None);
            innerHost.Dispose();
        }

        EnqueueLog(runId, logLines,
            $"[{DateTime.UtcNow:HH:mm:ss}] CLI exit code: {cliResult.ExitCode}");

        var isKnownCrash = cliResult.ExitCode != 0 && cliResult.IsKnownPostBacktestCrash;

        if (cliResult.ExitCode != 0 && !isKnownCrash)
        {
            var errMsg = !string.IsNullOrWhiteSpace(cliResult.StandardError)
                ? cliResult.StandardError.Trim()
                : cliResult.StandardOutput.Split('\n').LastOrDefault(l => l.Contains("Error"))?.Trim()
                    ?? $"ctrader-cli exited with code {cliResult.ExitCode}";
            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] CLI error: {errMsg}");
        }

        var reportHtmlPath = "";
        try
        {
            var algoDir = Path.GetDirectoryName(algoPath)!;
            var dataSrcDir = Path.Combine(algoDir, "data", "src");
            if (Directory.Exists(dataSrcDir))
            {
                var backtestDirs = Directory.GetDirectories(dataSrcDir, "Backtesting", SearchOption.AllDirectories)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .ToList();

                if (backtestDirs.Count > 0)
                {
                    var latestDir = backtestDirs[0];
                    var htmlFile = Path.Combine(latestDir, "report.html");
                    if (File.Exists(htmlFile))
                    {
                        var destHtml = Path.Combine(resultsDir, "report.html");
                        File.Copy(htmlFile, destHtml, overwrite: true);
                        reportHtmlPath = destHtml;
                        EnqueueLog(runId, logLines,
                            $"[{DateTime.UtcNow:HH:mm:ss}] cTrader report: {destHtml}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            EnqueueLog(runId, logLines,
                $"[{DateTime.UtcNow:HH:mm:ss}] Failed to capture cTrader report: {ex.Message}");
        }

        return new BacktestResult
        {
            RunId      = runId,
            ExitCode   = isKnownCrash ? 0 : cliResult.ExitCode,
            AlgoHash   = algoHash,
            ErrorMessage = isKnownCrash ? null : (cliResult.ExitCode != 0
                ? cliResult.StandardError.Trim() ?? $"CLI exited with code {cliResult.ExitCode}"
                : null),
        };
    }

    private static (int dataPort, int commandPort) AllocatePorts()
    {
        using var a = new TcpListener(IPAddress.Loopback, 0);
        using var b = new TcpListener(IPAddress.Loopback, 0);
        a.Start(); b.Start();
        var p1 = ((IPEndPoint)a.LocalEndpoint!).Port;
        var p2 = ((IPEndPoint)b.LocalEndpoint!).Port;
        a.Stop(); b.Stop();
        return (p1, p2);
    }

    private string ResolveAlgoPath()
    {
        var configured = _configuration["CTrader:AlgoPath"];
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Release", "net6.0", "src.algo")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Debug", "net6.0", "src.algo")),
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("src.algo not found. Build TradingEngine.Adapters.CTrader first.");
    }

    private static string ComputeAlgoHash(string algoPath)
    {
        if (!File.Exists(algoPath)) return "missing";
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(algoPath);
        return Convert.ToHexString(sha.ComputeHash(fs))[..16].ToLowerInvariant();
    }

    private async Task<TradeStats> GetTradeStatsAsync(string runId, decimal initialBalance)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var trades = await db.Trades
                .Where(t => t.RunId == runId)
                .OrderBy(t => t.ClosedAtUtc)
                .ToListAsync();

            if (trades.Count == 0) return new(0, 0, 0, 0, 0);

            var netPnL  = trades.Sum(t => t.NetPnLAmount);
            var wins    = trades.Count(t => t.NetPnLAmount > 0);
            var winRate = (double)wins / trades.Count;

            var equity = initialBalance;
            var peak   = initialBalance;
            var maxDd  = 0m;
            foreach (var t in trades)
            {
                equity += t.NetPnLAmount;
                if (equity > peak) peak = equity;
                if (peak > 0)
                {
                    var dd = (peak - equity) / peak;
                    if (dd > maxDd) maxDd = dd;
                }
            }

            return new(netPnL, maxDd, trades.Count, wins, winRate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query trade stats for {RunId}", runId);
            return new(0, 0, 0, 0, 0);
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingEngine.CTraderRunner;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.Events;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Services;

namespace TradingEngine.Tests.Simulation.Harness;

[CollectionDefinition("CtraderSerial", DisableParallelization = true)]
public sealed class CtraderSerialCollection : ICollectionFixture<CtraderTestFixture> { }

public sealed class CtraderTestFixture : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        CtraderProcessGuard.KillStrays();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        var count = CtraderProcessGuard.StrayCount();
        if (count > 0)
        {
            CtraderProcessGuard.KillStrays();
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Shared harness for in-process engine + cTrader CLI diagnostic tests.
/// Mirrors the dashboard backtest flow: EngineHostFactory.Create + CTraderBrokerAdapter.
/// </summary>
public sealed class CtraderTestHarness : IAsyncDisposable
{
    public RunArtifacts Artifacts { get; }
    private IHost? _host;

    public string DbPath => Artifacts.DbPath;
    public string RunId => Artifacts.RunId;

    public CtraderTestHarness(string testName = "ctrader")
    {
        Artifacts = RunArtifacts.Create(testName);
    }

    public record Result(
        int Trades, int BarEvals, int Signals, int Orders, int Execs,
        int CliExitCode, string CliStderr, string RunId,
        IReadOnlyList<TradeRow>? TradeRows = null,
        string? ReportJsonPath = null);

    public record TradeRow(
        string Symbol, string Direction, decimal Lots,
        decimal EntryPrice, decimal ExitPrice, decimal NetPnL,
        decimal Commission, decimal Swap, string ExitReason);

    // ─── credentials ────────────────────────────────────────────────
    public static string ResolveCredential(string key, string envKey)
    {
        var solutionRoot = SolutionRoot;
        var devSettingsPath = Path.Combine(solutionRoot, "src", "TradingEngine.Web", "appsettings.Development.json");
        if (File.Exists(devSettingsPath))
        {
            var devConfig = new ConfigurationBuilder().AddJsonFile(devSettingsPath).Build();
            var value = devConfig[$"CTrader:{key}"];
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return Environment.GetEnvironmentVariable(envKey) ?? "";
    }

    public static string SolutionRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    public static string ResolveAlgo()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Debug", "net6.0", "src.algo")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Release", "net6.0", "src.algo")),
        };
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("src.algo not found");
    }

    // ─── main run method ────────────────────────────────────────────

    public async Task<Result> RunAsync(
        string symbol, string period, DateTime start, DateTime end, string label,
        CancellationToken ct = default)
    {
        var ctid = ResolveCredential("CtId", "CTrader__CtId");
        var pwdFile = ResolveCredential("PwdFile", "CTrader__PwdFile");
        var account = ResolveCredential("Account", "CTrader__Account");
        if (string.IsNullOrEmpty(ctid)) throw new InvalidOperationException("No cTrader credentials");

        var runId = Artifacts.RunId;
        var algoPath = ResolveAlgo();

        // Dynamic ports
        using var a = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        using var b = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        a.Start(); b.Start();
        var dataPort = ((System.Net.IPEndPoint)a.LocalEndpoint).Port;
        var commandPort = ((System.Net.IPEndPoint)b.LocalEndpoint).Port;
        a.Stop(); b.Stop();

        var signalCount = 0;
        var orderCount = 0;
        var execCount = 0;
        var diagLog = new ConcurrentQueue<string>();

        Log(diagLog, $"[{label}] Starting. {symbol}/{period} {start:yyyy-MM-dd}->{end:yyyy-MM-dd} Ports={dataPort}/{commandPort} RunId={runId}");

        var progressCallback = new Progress<BacktestProgressEvent>(evt =>
        {
            if (evt.EventType == "SIGNAL")
            {
                Interlocked.Increment(ref signalCount);
                Log(diagLog, $"  SIGNAL #{signalCount}: {evt.Message}");
            }
            else if (evt.EventType == "ORDER")
            {
                Interlocked.Increment(ref orderCount);
                Log(diagLog, $"  ORDER #{orderCount}: {evt.Message}");
            }
            else if (evt.EventType == "EXEC" || evt.EventType == "REJECTED")
            {
                Interlocked.Increment(ref execCount);
                Log(diagLog, $"  EXEC #{execCount} ({evt.EventType}): {evt.Message}");
            }
        });

        // Build strategies adapted to the test timeframe
        var preloadedConfig = BuildTimeframeConfig(symbol, period);

        _host = EngineHostFactory.Create(new EngineHostOptions
        {
            RunId = runId,
            Mode = EngineMode.Backtest,
            AdapterFactory = sp =>
            {
                var transportLogger = Substitute.For<ILogger<NetMqMessageTransport>>();
                var adapterLogger = Substitute.For<ILogger<CTraderBrokerAdapter>>();
                var transport = new NetMqMessageTransport(
                    $"tcp://127.0.0.1:{dataPort}",
                    $"tcp://*:{commandPort}",
                    transportLogger);
                return new CTraderBrokerAdapter(transport, adapterLogger);
            },
            DbPath = DbPath,
            SolutionRoot = SolutionRoot,
            SymbolNames = new[] { symbol },
            Progress = progressCallback,
            MinLogLevel = LogLevel.Warning,
            PreloadedConfig = preloadedConfig,
        });
        EngineHostFactory.WireEventHandlers(_host);
        EngineHostFactory.WireRiskRules(_host);

        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
        }

        // Start engine
        var sw = Stopwatch.StartNew();
        try
        {
            await _host.StartAsync(ct);
        }
        catch (Exception ex)
        {
            Log(diagLog, $"[{label}] ENGINE START FAILED: {ex.Message}");
            _host.Dispose();
            return new Result(0, 0, signalCount, orderCount, execCount, -1, ex.Message, runId);
        }
        Log(diagLog, $"[{label}] Engine started in {sw.Elapsed.TotalSeconds:F1}s");

        // Launch cTrader CLI via BacktestCli (Process.Start, single code path).
        var cliRequest = new BacktestCliRequest
        {
            AlgoPath = algoPath,
            Symbol = symbol,
            Period = period,
            Start = start,
            End = end,
            CtId = ctid,
            PwdFile = pwdFile,
            Account = account,
            DataPort = dataPort,
            CommandPort = commandPort,
            Balance = 100_000m,
            CommissionPerMillion = 30m,
            SpreadPips = 1m,
            DataMode = "m1",
            Symbols = new[] { symbol },
            Periods = new[] { period.ToUpperInvariant() },
            FullAccess = true,
        };

        Log(diagLog, $"[{label}] Launching ctrader-cli...");
        var cliSw = Stopwatch.StartNew();
        BacktestCliResult cliResult;
        try
        {
            cliResult = await BacktestCli.InvokeAsync(cliRequest, ct);
        }
        finally
        {
            cliSw.Stop();
        }
        sw.Stop();

        var cliExitCode = cliResult.ExitCode;
        Log(diagLog, $"[{label}] CLI exit={cliExitCode} in {cliSw.Elapsed.TotalSeconds:F1}s (total {sw.Elapsed.TotalSeconds:F1}s)");

        if (cliExitCode != 0)
        {
            var stderr = cliResult.StdErr.Length > 500 ? cliResult.StdErr[..500] : cliResult.StdErr;
            Log(diagLog, $"[{label}] CLI STDERR (exit code={cliExitCode}): {stderr}");
            Console.WriteLine($"[{label}] CLI stderr: {stderr}");
        }

        Log(diagLog, $"[{label}] CBOT lines: {cliResult.CbotLines.Count}");
        foreach (var line in cliResult.CbotLines.TakeLast(10))
            Log(diagLog, $"  {line}");

        if (!File.Exists(Artifacts.EventsJsonPath))
        {
            try
            {
                var algoDir = Path.GetDirectoryName(algoPath)!;
                var dataSrcDir = Path.Combine(algoDir, "data", "src");
                if (Directory.Exists(dataSrcDir))
                {
                    var backtestDirs = Directory.GetDirectories(dataSrcDir, "Backtesting", SearchOption.AllDirectories)
                        .OrderByDescending(Directory.GetLastWriteTimeUtc)
                        .ToList();
                    foreach (var dir in backtestDirs)
                    {
                        var jsonFile = Path.Combine(dir, "events.json");
                        if (!File.Exists(jsonFile)) continue;
                        File.Copy(jsonFile, Artifacts.EventsJsonPath, overwrite: true);
                        Log(diagLog, $"[{label}] cTrader events JSON ({new FileInfo(jsonFile).Length} bytes): {Artifacts.EventsJsonPath}");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log(diagLog, $"[{label}] Failed to capture cTrader events: {ex.Message}");
            }
        }

        // Query DB before stopping host
        await Task.Delay(2000, ct);
        int tradeCount = 0, barEvalCount = 0;
        var tradeRows = new List<TradeRow>();
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            tradeCount = await db.Trades.CountAsync(t => t.RunId == runId, ct);
            barEvalCount = await db.BarEvaluations.CountAsync(e => e.RunId == runId, ct);
            Log(diagLog, $"[{label}] DB: Trades={tradeCount} BarEvals={barEvalCount}");

            if (tradeCount > 0)
            {
                var trades = await db.Trades.Where(t => t.RunId == runId)
                    .OrderBy(t => t.ClosedAtUtc).ToListAsync(ct);
                foreach (var t in trades)
                {
                    tradeRows.Add(new TradeRow(t.Symbol, t.Direction, t.Lots,
                        t.EntryPrice, t.ExitPrice, t.NetPnLAmount, t.CommissionAmount, t.SwapAmount, t.ExitReason));
                    Log(diagLog, $"  Trade: {t.Symbol} {t.Direction} pnl={t.NetPnLAmount:F2} lots={t.Lots} entry={t.EntryPrice:F5} exit={t.ExitPrice:F5} reason={t.ExitReason}");
                }
            }
        }

        // Flush remaining diagnostics
        while (diagLog.TryDequeue(out var line))
            Console.WriteLine(line);

        await _host.StopAsync(CancellationToken.None);

        return new Result(tradeCount, barEvalCount, signalCount, orderCount, execCount,
            cliExitCode, cliResult.StdErr, runId, tradeRows,
            File.Exists(Artifacts.EventsJsonPath) ? Artifacts.EventsJsonPath : null);
    }

    // ─── timeframe-adapted config ───────────────────────────────────

    private static LoadedConfig BuildTimeframeConfig(string symbol, string period)
    {
        var baseConfig = new ConfigLoader(SolutionRoot).Load();
        var tf = Enum.TryParse<Timeframe>(period, ignoreCase: true, out var t) ? t : Timeframe.H1;

        // Clone strategy configs with the test's timeframe
        var adapted = baseConfig.StrategyConfigs.Select(s => new StrategyConfigEntry(
            s.Id, s.DisplayName, s.Enabled, s.Symbols, s.RiskProfileId, s.Parameters,
            period.ToUpperInvariant())
        {
            RegimeFilter = s.RegimeFilter,
            OrderEntry = s.OrderEntry,
            PositionManagement = s.PositionManagement,
            Reentry = s.Reentry,
        }).ToList();

        return new LoadedConfig(baseConfig.PropFirms, baseConfig.RiskProfiles)
        {
            StrategyConfigs = adapted,
            NewsWindows = baseConfig.NewsWindows,
            StrategyRotation = baseConfig.StrategyRotation,
            Governor = baseConfig.Governor,
            SizingPolicy = baseConfig.SizingPolicy,
        };
    }

    private static void Log(ConcurrentQueue<string> log, string msg)
    {
        Console.WriteLine(msg);
        log.Enqueue(msg);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            try { await _host.StopAsync(CancellationToken.None); } catch { }
            _host.Dispose();
        }
        await Artifacts.DisposeAsync();
    }
}

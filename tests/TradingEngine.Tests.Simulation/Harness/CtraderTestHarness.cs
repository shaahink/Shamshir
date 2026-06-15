using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly string _dbPath;
    private IHost? _host;

    public CtraderTestHarness()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ctrader_test_{Guid.NewGuid():N}.db");
    }

    public record Result(
        int Trades, int BarEvals, int Signals, int Orders, int Execs,
        int CliExitCode, string CliStderr, string RunId);

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

        var runId = Guid.NewGuid().ToString("N")[..8];
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
            DbPath = _dbPath,
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

        // Launch cTrader CLI
        var cli = new CTraderCli();
        var args = new[]
        {
            $"--start={start:dd/MM/yyyy}", $"--end={end:dd/MM/yyyy}",
            $"--symbol={symbol}", $"--period={period.ToLowerInvariant()}",
            "--balance=100000", "--commission=30", "--spread=1", "--data-mode=m1",
            $"--ctid={ctid}", $"--pwd-file={pwdFile}", $"--account={account}",
            $"--DataPort={dataPort}", $"--CommandPort={commandPort}",
            $"--SymbolString={symbol}", $"--Periods={period.ToUpperInvariant()}",
            "--full-access",
        };

        Log(diagLog, $"[{label}] Launching ctrader-cli...");
        var cliSw = Stopwatch.StartNew();
        CTraderResult cliResult;
        try
        {
            cliResult = await cli.BacktestAsync(algoPath, args, ct);
        }
        finally
        {
            cliSw.Stop();
        }
        sw.Stop();

        Log(diagLog, $"[{label}] CLI exit={cliResult.ExitCode} in {cliSw.Elapsed.TotalSeconds:F1}s (total {sw.Elapsed.TotalSeconds:F1}s)");

        if (cliResult.ExitCode != 0)
        {
            var stderr = cliResult.StandardError.Length > 500
                ? cliResult.StandardError[..500] : cliResult.StandardError;
            Log(diagLog, $"[{label}] CLI STDERR: {stderr}");
            Console.WriteLine($"[{label}] CLI stderr: {stderr}");
        }

        var cbotLines = cliResult.StandardOutput.Split('\n').Where(l => l.Contains("CBOT|")).ToList();
        Log(diagLog, $"[{label}] CBOT lines: {cbotLines.Count}");
        foreach (var line in cbotLines.TakeLast(10))
            Log(diagLog, $"  {line.Trim()}");

        // Query DB before stopping host
        await Task.Delay(2000, ct);
        int tradeCount = 0, barEvalCount = 0;
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
                    Log(diagLog, $"  Trade: {t.Symbol} {t.Direction} pnl={t.NetPnLAmount:F2} lots={t.Lots}");
            }
        }

        // Flush remaining diagnostics
        while (diagLog.TryDequeue(out var line))
            Console.WriteLine(line);

        await _host.StopAsync(CancellationToken.None);

        return new Result(tradeCount, barEvalCount, signalCount, orderCount, execCount,
            cliResult.ExitCode, cliResult.StandardError, runId);
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

        return new LoadedConfig(baseConfig.PropFirms, baseConfig.RiskProfiles, adapted)
        {
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
        for (var i = 0; i < 10 && File.Exists(_dbPath); i++)
        {
            try { File.Delete(_dbPath); break; }
            catch (IOException) { await Task.Delay(200); }
        }
    }
}

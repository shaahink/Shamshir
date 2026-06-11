using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.CTraderRunner;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.Events;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Risk;
using TradingEngine.Risk.Filters;
using TradingEngine.Services;

namespace TradingEngine.Tests.Simulation.Pipeline;

public sealed class CtraderPipelineDiagnosticTest : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly ConcurrentQueue<string> _log = new();

    public CtraderPipelineDiagnosticTest()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ctrader_diag_{Guid.NewGuid():N}.db");
    }

    private static string ResolveCredential(string key, string envKey)
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var devSettingsPath = Path.Combine(solutionRoot, "src", "TradingEngine.Web", "appsettings.Development.json");
        if (File.Exists(devSettingsPath))
        {
            var devConfig = new ConfigurationBuilder().AddJsonFile(devSettingsPath).Build();
            var value = devConfig[$"CTrader:{key}"];
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return Environment.GetEnvironmentVariable(envKey) ?? "";
    }

    private static string ResolveAlgo()
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

    private async Task<(int trades, int barEvals, int signals, int orders, int execs)> RunDiagnostic(
        string symbol, string period, DateTime start, DateTime end, string label)
    {
        var ctid = ResolveCredential("CtId", "CTrader__CtId");
        var pwdFile = ResolveCredential("PwdFile", "CTrader__PwdFile");
        var account = ResolveCredential("Account", "CTrader__Account");
        if (string.IsNullOrEmpty(ctid)) throw new InvalidOperationException("No credentials");

        var runId = Guid.NewGuid().ToString("N")[..8];
        var algoPath = ResolveAlgo();
        var sym = Symbol.Parse(symbol);

        // Dynamic ports
        using var a = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        using var b = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        a.Start(); b.Start();
        var dataPort = ((System.Net.IPEndPoint)a.LocalEndpoint).Port;
        var commandPort = ((System.Net.IPEndPoint)b.LocalEndpoint).Port;
        a.Stop(); b.Stop();

        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        var signalCount = 0;
        var orderCount = 0;
        var execCount = 0;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

        var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureServices((_, services) =>
            {
                services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
                services.AddSingleton(new EngineRunContext(runId));
                services.AddSingleton<IBrokerAdapter>(sp =>
                    new NetMQBrokerAdapter($"tcp://127.0.0.1:{dataPort}",
                        $"tcp://*:{commandPort}",
                        sp.GetRequiredService<ILogger<NetMQBrokerAdapter>>()));

                var symbolInfo = new SymbolInfo(sym, SymbolCategory.Forex, "EUR", "USD",
                    0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);
                var symbolRegistry = new SymbolInfoRegistry();
                symbolRegistry.Register(symbolInfo);
                services.AddSingleton<ISymbolInfoRegistry>(_ => symbolRegistry);
                services.AddSingleton(new CrossRateStore());
                services.AddSingleton<Func<string, string, decimal>>(_ => new CrossRateStore().Convert);
                services.AddSingleton<INewsFilter>(_ => new NewsFilter());
                services.AddSingleton<SessionFilter>();
                services.AddSingleton<DrawdownTracker>();
                services.AddSingleton<RiskManager>();
                services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<RiskManager>());
                var configLoader = new ConfigLoader(solutionRoot);
                var loadedConfig = configLoader.Load();
                services.AddSingleton(loadedConfig);
                services.AddSingleton<IRiskProfileResolver>(sp =>
                    new RiskProfileResolver(sp.GetRequiredService<LoadedConfig>().RiskProfiles));
                services.AddSingleton<IEngineClock, BrokerClock>();
                services.AddDbContext<TradingDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
                services.AddScoped<ITradeRepository, SqliteTradeRepository>();
                services.AddScoped<IEquityRepository, SqliteEquityRepository>();
                services.AddSingleton<PersistenceService>();
                services.AddSingleton<IPositionManager, PositionManager>();
                services.AddSingleton<IEventBus, TypedEventBus>();
                services.AddSingleton<EquityPersistenceHandler>();
                services.AddSingleton<TradePersistenceHandler>();
                services.AddSingleton<BarEvaluationHandler>();
                services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
                services.AddSingleton<OrderDispatcher>();
                services.AddSingleton<PositionTracker>();

                // Trace progress events
                services.AddSingleton<IProgress<BacktestProgressEvent>>(_ =>
                    new Progress<BacktestProgressEvent>(evt =>
                    {
                        if (evt.EventType == "SIGNAL")
                        {
                            Interlocked.Increment(ref signalCount);
                            Console.WriteLine($"[TRACE] SIGNAL #{signalCount}: {evt.Message}");
                        }
                        else if (evt.EventType == "ORDER")
                        {
                            Interlocked.Increment(ref orderCount);
                            Console.WriteLine($"[TRACE] ORDER #{orderCount}: {evt.Message}");
                        }
                        else if (evt.EventType == "EXEC")
                        {
                            Interlocked.Increment(ref execCount);
                            Console.WriteLine($"[TRACE] EXEC #{execCount}: {evt.Message}");
                        }
                    }));

                var registry = new StrategyRegistry();
                services.AddSingleton(registry);
                services.AddSingleton<IEnumerable<IStrategy>>(sp =>
                {
                    var reg = sp.GetRequiredService<StrategyRegistry>();
                    var loaded = sp.GetRequiredService<LoadedConfig>();
                    var activeIds = loaded.StrategyConfigs.Select(c => c.Id).ToArray();
                    return reg.CreateStrategies(activeIds, loaded, sp);
                });

                services.AddSingleton<EngineWorker>(sp => new EngineWorker(
                    sp.GetRequiredService<IBrokerAdapter>(),
                    sp.GetRequiredService<IRiskManager>(),
                    sp.GetRequiredService<DrawdownTracker>(),
                    sp.GetRequiredService<IEnumerable<IStrategy>>(),
                    sp.GetRequiredService<IIndicatorService>(),
                    sp.GetRequiredService<IEventBus>(),
                    sp.GetRequiredService<IEngineClock>(),
                    sp.GetRequiredService<ISymbolInfoRegistry>(),
                    sp.GetRequiredService<IRiskProfileResolver>(),
                    sp.GetRequiredService<Func<string, string, decimal>>(),
                    sp.GetRequiredService<PersistenceService>(),
                    sp.GetRequiredService<OrderDispatcher>(),
                    sp.GetRequiredService<PositionTracker>(),
                    sp.GetRequiredService<ILogger<EngineWorker>>(),
                    sp.GetRequiredService<EngineRunContext>(),
                    sp.GetRequiredService<CrossRateStore>(),
                    dataFeed: null,
                    progress: sp.GetRequiredService<IProgress<BacktestProgressEvent>>()));
                services.AddHostedService<EngineWorker>(sp => sp.GetRequiredService<EngineWorker>());
            })
            .Build();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        // Subscribe handlers
        var eventBus = host.Services.GetRequiredService<IEventBus>();
        eventBus.Subscribe<TradeClosed>(host.Services.GetRequiredService<TradePersistenceHandler>());
        eventBus.Subscribe<BarEvaluated>(host.Services.GetRequiredService<BarEvaluationHandler>());
        eventBus.Subscribe<EquityUpdated>(host.Services.GetRequiredService<EquityPersistenceHandler>());

        var rm = host.Services.GetRequiredService<RiskManager>();
        var loaded = host.Services.GetRequiredService<LoadedConfig>();
        var activeRiskProfileId = loaded.StrategyConfigs.Select(c => c.RiskProfileId).FirstOrDefault() ?? "standard";
        var activeProfile = loaded.RiskProfiles.FirstOrDefault(r => r.Id == activeRiskProfileId);
        var activeRuleSetId = activeProfile?.PropFirmRuleSetId ?? "ftmo-standard";
        var ruleSet = loaded.PropFirms.FirstOrDefault(r => r.Id == activeRuleSetId);
        if (ruleSet is not null) rm.SetActiveRuleSet(ruleSet);

        Console.WriteLine($"[TEST:{label}] Starting. Symbol={symbol} Period={period} {start:yyyy-MM-dd}→{end:yyyy-MM-dd} Ports={dataPort}/{commandPort}");
        Console.WriteLine($"[TEST:{label}] RunId={runId} DB={_dbPath}");

        // Start engine
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await host.StartAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TEST:{label}] ENGINE START FAILED: {ex.Message}");
            host.Dispose();
            return (0, 0, 0, 0, 0);
        }
        Console.WriteLine($"[TEST:{label}] Engine started in {sw.Elapsed.TotalSeconds:F1}s");

        // Launch CLI
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

        Console.WriteLine($"[TEST:{label}] Launching ctrader-cli...");
        var cliSw = System.Diagnostics.Stopwatch.StartNew();
        CTraderResult cliResult;
        try
        {
            cliResult = await cli.BacktestAsync(algoPath, args, cts.Token);
        }
        finally
        {
            cliSw.Stop();
        }
        sw.Stop();

        Console.WriteLine($"[TEST:{label}] CLI exit={cliResult.ExitCode} in {cliSw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"[TEST:{label}] Total={sw.Elapsed.TotalSeconds:F1}s Signals={signalCount} Orders={orderCount} Execs={execCount}");

        if (cliResult.ExitCode != 0)
        {
            var stderr = cliResult.StandardError.Length > 1000
                ? cliResult.StandardError[..1000] : cliResult.StandardError;
            Console.WriteLine($"[TEST:{label}] CLI STDERR: {stderr}");
        }

        var cbotLines = cliResult.StandardOutput.Split('\n').Where(l => l.Contains("CBOT|")).ToList();
        Console.WriteLine($"[TEST:{label}] CBOT lines: {cbotLines.Count}");
        foreach (var line in cbotLines.TakeLast(10))
            Console.WriteLine($"  {line.Trim()}");

        // Check DB BEFORE stopping host
        await Task.Delay(2000);
        int tradeCount = 0, barEvalCount = 0;
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            tradeCount = await db.Trades.CountAsync(t => t.RunId == runId);
            barEvalCount = await db.BarEvaluations.CountAsync(e => e.RunId == runId);
            Console.WriteLine($"[TEST:{label}] DB: Trades={tradeCount} BarEvals={barEvalCount}");

            if (tradeCount > 0)
            {
                var trades = await db.Trades.Where(t => t.RunId == runId).OrderBy(t => t.ClosedAtUtc).ToListAsync();
                foreach (var t in trades)
                    Console.WriteLine($"  Trade: {t.Symbol} {t.Direction} pnl={t.NetPnLAmount:F2} lots={t.Lots}");
            }
        }

        await host.StopAsync(CancellationToken.None);
        host.Dispose();

        return (tradeCount, barEvalCount, signalCount, orderCount, execCount);
    }

    [Fact(Timeout = 300_000)]
    public async Task EurUsd_H1_30Days_MirrorsWebDefault_ProducesTrades()
    {
        var (trades, bars, signals, orders, execs) = await RunDiagnostic(
            "EURUSD", "H1",
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 31),
            "EURUSD-30D");

        Console.WriteLine($"[RESULT:EURUSD-30D] Trades={trades} BarEvals={bars} Signals={signals} Orders={orders} Execs={execs}");
        bars.Should().BeGreaterThan(0, "bar evaluations must exist");
        trades.Should().BeGreaterThan(0, "at least one trade expected in 30 days");
    }

    [Fact(Timeout = 180_000)]
    public async Task EurUsd_H1_3Days_ProducesTrades()
    {
        var (trades, bars, signals, orders, execs) = await RunDiagnostic(
            "EURUSD", "H1",
            new DateTime(2024, 1, 15), new DateTime(2024, 1, 18),
            "EURUSD-3D");

        Console.WriteLine($"[RESULT:EURUSD-3D] Trades={trades} BarEvals={bars} Signals={signals} Orders={orders} Execs={execs}");
        bars.Should().BeGreaterThan(0);
        trades.Should().BeGreaterThan(0, "at least one trade expected in 3 days");
    }

    [Fact(Timeout = 300_000)]
    public async Task GbpUsd_H1_30Days_ProducesTrades()
    {
        var (trades, bars, signals, orders, execs) = await RunDiagnostic(
            "GBPUSD", "H1",
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 31),
            "GBPUSD-30D");

        Console.WriteLine($"[RESULT:GBPUSD-30D] Trades={trades} BarEvals={bars} Signals={signals} Orders={orders} Execs={execs}");
        bars.Should().BeGreaterThan(0);
        trades.Should().BeGreaterThan(0, "at least one trade expected in 30 days");
    }

    [Fact(Timeout = 180_000)]
    public async Task EurUsd_M15_3Days_ProducesTrades()
    {
        var (trades, bars, signals, orders, execs) = await RunDiagnostic(
            "EURUSD", "M15",
            new DateTime(2024, 1, 15), new DateTime(2024, 1, 18),
            "EURUSD-M15-3D");

        Console.WriteLine($"[RESULT:EURUSD-M15-3D] Trades={trades} BarEvals={bars} Signals={signals} Orders={orders} Execs={execs}");
        bars.Should().BeGreaterThan(0);
        trades.Should().BeGreaterThan(0, "at least one trade expected with M15");
    }

    public async ValueTask DisposeAsync()
    {
        for (var i = 0; i < 10 && File.Exists(_dbPath); i++)
        {
            try { File.Delete(_dbPath); break; }
            catch (IOException) { await Task.Delay(200); }
        }
    }
}

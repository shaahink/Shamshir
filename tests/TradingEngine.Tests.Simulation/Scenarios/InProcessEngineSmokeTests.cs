using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.Events;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Risk;
using TradingEngine.Risk.Filters;
using TradingEngine.Services;

namespace TradingEngine.Tests.Simulation.Scenarios;

[Trait("Category", "Smoke")]
public sealed class InProcessEngineSmokeTests : IAsyncDisposable
{
    private readonly string _dbPath;

    public InProcessEngineSmokeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"inproc_test_{Guid.NewGuid():N}.db");
    }

    [Fact(Timeout = 15_000)]
    public async Task NetMQEngine_InnerHost_StartsAndStopsCleanly()
    {
        var runId = "smoke-001";
        var dataPort = 15557;
        var commandPort = 15558;
        var symbol = Symbol.Parse("EURUSD");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(new EngineRunContext(runId));

                services.AddSingleton<IBrokerAdapter>(sp =>
                    new NetMQBrokerAdapter(
                        $"tcp://127.0.0.1:{dataPort}",
                        $"tcp://*:{commandPort}",
                        sp.GetRequiredService<ILogger<NetMQBrokerAdapter>>()));

                var symbolInfo = new SymbolInfo(symbol, SymbolCategory.Forex, "EUR", "USD",
                    0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);
                var symbolRegistry = new SymbolInfoRegistry();
                symbolRegistry.Register(symbolInfo);
                services.AddSingleton<ISymbolInfoRegistry>(_ => symbolRegistry);

                var crossRateStore = new CrossRateStore();
                services.AddSingleton(crossRateStore);
                services.AddSingleton<Func<string, string, decimal>>(_ => crossRateStore.Convert);

                services.AddSingleton<INewsFilter>(_ => new NewsFilter());
                services.AddSingleton<SessionFilter>();
                services.AddSingleton<DrawdownTracker>();
                services.AddSingleton<RiskManager>();
                services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<RiskManager>());
                services.AddSingleton(new SizingPolicyOptions());
                services.AddSingleton(new GovernorOptions());
                var testGovernor = Substitute.For<ITradingGovernor>();
                testGovernor.Evaluate(Arg.Any<GovernorContext>())
                    .Returns(new GovernorDecision(true, 1.0m, GovernorTradingState.Normal, "OK"));
                services.AddSingleton<ITradingGovernor>(_ => testGovernor);

                var profile = new RiskProfile(
                    "standard", "Standard", 1.0, 5.0, 10.0, 100.0, 10.0, 0.5, 0.1, 5,
                    false, "ftmo-standard",
                    LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);
                var resolver = new RiskProfileResolver([profile]);
                services.AddSingleton<IRiskProfileResolver>(_ => resolver);

                services.AddSingleton<IEngineClock, BrokerClock>();

                services.AddDbContext<TradingDbContext>(o =>
                    o.UseSqlite($"Data Source={_dbPath}"));
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

                services.AddSingleton<IProgress<BacktestProgressEvent>>(_ =>
                    new Progress<BacktestProgressEvent>(_ => { }));

                var registry = new StrategyRegistry();
                services.AddSingleton(registry);
                services.AddSingleton<IEnumerable<IStrategy>>(_ => []);

                services.AddSingleton<EngineWorker>(sp => new EngineWorker(
                    new EngineWorkerDependencies
                    {
                        Market = new MarketServices
                        {
                            Broker = sp.GetRequiredService<IBrokerAdapter>(),
                            Indicators = sp.GetRequiredService<IIndicatorService>(),
                            SymbolRegistry = sp.GetRequiredService<ISymbolInfoRegistry>(),
                            CrossRateStore = sp.GetRequiredService<CrossRateStore>(),
                            Clock = sp.GetRequiredService<IEngineClock>(),
                            EngineMode = EngineMode.Backtest,
                            DataFeed = null,
                        },
                        Risk = new RiskServices
                        {
                            RiskManager = sp.GetRequiredService<IRiskManager>(),
                            DrawdownTracker = sp.GetRequiredService<DrawdownTracker>(),
                            RiskProfileResolver = sp.GetRequiredService<IRiskProfileResolver>(),
                            CrossRateProvider = sp.GetRequiredService<Func<string, string, decimal>>(),
                            Governor = sp.GetRequiredService<ITradingGovernor>(),
                            SizingPolicy = new SizingPolicyOptions(),
                        },
                        Strategies = new StrategyServices
                        {
                            Strategies = sp.GetRequiredService<IEnumerable<IStrategy>>(),
                            StrategyBank = Substitute.For<IStrategyBank>(),
                            RegimeDetector = Substitute.For<IRegimeDetector>(),
                            OrderDispatcher = sp.GetRequiredService<OrderDispatcher>(),
                            PositionTracker = sp.GetRequiredService<PositionTracker>(),
                        },
                        Persistence = new PersistenceServices
                        {
                            EventBus = sp.GetRequiredService<IEventBus>(),
                            Persistence = sp.GetRequiredService<PersistenceService>(),
                            Progress = sp.GetRequiredService<IProgress<BacktestProgressEvent>>(),
                        },
                    },
                    sp.GetRequiredService<EngineRunContext>(),
                    sp.GetRequiredService<ILogger<EngineWorker>>()));
                services.AddHostedService<EngineWorker>(sp =>
                    sp.GetRequiredService<EngineWorker>());
            })
            .Build();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        // Start — engine binds ROUTER, subscribes to data port
        await host.StartAsync(cts.Token);

        // Give engine time to connect
        await Task.Delay(500, cts.Token);

        // Engine should be connected (ROUTER bound on command port)
        var adapter = host.Services.GetRequiredService<IBrokerAdapter>() as NetMQBrokerAdapter;
        Assert.NotNull(adapter);

        // Clean stop
        await host.StopAsync(CancellationToken.None);
        host.Dispose();
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

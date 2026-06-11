using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Events;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Services;

namespace TradingEngine.Tests.Simulation.Harness;

public sealed class ReplayTestHarness : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly string _dbPath;

    private ReplayTestHarness(IHost host, string dbPath)
    {
        _host = host;
        _dbPath = dbPath;
    }

    public IServiceProvider Services => _host.Services;

    public static async Task<ReplayTestHarness> CreateAsync(
        IReadOnlyList<Bar> bars,
        string runId = "test-run-1")
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"replay_test_{Guid.NewGuid():N}.db");
        var symbol = bars[0].Symbol;
        var from = bars[0].OpenTimeUtc;
        var to = bars[^1].OpenTimeUtc;

        var host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(new EngineRunContext(runId));

                var barRepo = Substitute.For<IBarRepository>();
                barRepo.GetAsync(Arg.Any<Symbol>(), Arg.Any<Timeframe>(),
                    Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(bars));
                services.AddSingleton<IBarRepository>(_ => barRepo);
                services.AddSingleton<IBrokerAdapter>(sp => new BacktestReplayAdapter(
                    barRepo, symbol, Timeframe.H1, from, to,
                    10_000m, sp.GetRequiredService<ILogger<BacktestReplayAdapter>>()));

                var riskManager = Substitute.For<IRiskManager>();
                riskManager.CalculateLotSize(Arg.Any<TradeIntent>(),
                    Arg.Any<EquitySnapshot>(), Arg.Any<RiskProfile>(), Arg.Any<decimal>())
                    .Returns(0.01m);
                riskManager.Validate(Arg.Any<TradeIntent>(),
                    Arg.Any<EquitySnapshot>(), Arg.Any<RiskProfile>())
                    .Returns(Array.Empty<RiskViolation>());
                riskManager.ConsumeForceClosePending().Returns(false);
                riskManager.InitialBalance.Returns(10_000m);
                riskManager.CurrentState.Returns(
                    new RiskState(false, false, null, 0m, 0m, 0m, 0m, null));
                services.AddSingleton<IRiskManager>(_ => riskManager);
                services.AddSingleton<DrawdownTracker>();

                var symbolInfo = new SymbolInfo(symbol, SymbolCategory.Forex, "EUR", "USD",
                    0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);
                var symbolRegistry = Substitute.For<ISymbolInfoRegistry>();
                symbolRegistry.Get(symbol).Returns(symbolInfo);
                services.AddSingleton<ISymbolInfoRegistry>(_ => symbolRegistry);

                services.AddSingleton<Func<string, string, decimal>>(_ => (_, _) => 1.0m);
                services.AddSingleton(new CrossRateStore());

                var profile = new RiskProfile(
                    "standard", "Standard", 1.0, 5.0, 10.0, 100.0, 10.0, 0.5, 0.1, 5,
                    false, "ftmo-standard",
                    LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);
                var resolver = new RiskProfileResolver([profile]);
                services.AddSingleton<IRiskProfileResolver>(_ => resolver);

                services.AddSingleton<IEngineClock, BrokerClock>();

                services.AddDbContext<TradingDbContext>(o =>
                    o.UseSqlite($"Data Source={dbPath}"));
                services.AddScoped<ITradeRepository, SqliteTradeRepository>();
                services.AddScoped<IEquityRepository, SqliteEquityRepository>();
                services.AddSingleton<PersistenceService>();

                services.AddSingleton<IEventBus, TypedEventBus>();
                services.AddSingleton<TradePersistenceHandler>();
                services.AddSingleton<BarEvaluationHandler>();
                services.AddSingleton<EquityPersistenceHandler>();

                services.AddSingleton<IIndicatorService, SkenderIndicatorService>();

                services.AddSingleton<OrderDispatcher>();
                services.AddSingleton<PositionTracker>();
                services.AddSingleton<IPositionManager, PositionManager>();

                var strategy = new AlwaysSignalStrategy();
                services.AddSingleton<IEnumerable<IStrategy>>(_ => [strategy]);

                services.AddSingleton<EngineWorker>();
                services.AddHostedService<EngineWorker>(sp => sp.GetRequiredService<EngineWorker>());
            })
            .Build();

        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        var eventBus = host.Services.GetRequiredService<IEventBus>();
        eventBus.Subscribe<EquityUpdated>(
            host.Services.GetRequiredService<EquityPersistenceHandler>());
        eventBus.Subscribe<TradeClosed>(
            host.Services.GetRequiredService<TradePersistenceHandler>());
        eventBus.Subscribe<BarEvaluated>(
            host.Services.GetRequiredService<BarEvaluationHandler>());

        return new ReplayTestHarness(host, dbPath);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await _host.StartAsync(ct);

        try
        {
            var adapter = _host.Services.GetRequiredService<IBrokerAdapter>();
            await adapter.BarStream.Completion;
            await Task.Delay(5_000, ct);
        }
        finally
        {
            await _host.StopAsync(CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();

        for (var i = 0; i < 10 && File.Exists(_dbPath); i++)
        {
            try { File.Delete(_dbPath); break; }
            catch (IOException) { await Task.Delay(200); }
        }
    }
}

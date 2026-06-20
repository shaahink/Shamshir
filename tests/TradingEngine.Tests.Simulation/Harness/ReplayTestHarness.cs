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
                    10_000m, sp.GetRequiredService<ISymbolInfoRegistry>(),
                    sp.GetRequiredService<Func<string, string, decimal>>(),
                    sp.GetRequiredService<ILogger<BacktestReplayAdapter>>()));

                var riskManager = Substitute.For<IRiskManager>();
                riskManager.CalculateLotSize(Arg.Any<TradeIntent>(),
                    Arg.Any<EquitySnapshot>(), Arg.Any<RiskProfile>(), Arg.Any<decimal>())
                    .Returns(0.01m);
                riskManager.Validate(Arg.Any<TradeIntent>(),
                    Arg.Any<EquitySnapshot>(), Arg.Any<RiskProfile>(), Arg.Any<decimal>())
                    .Returns(Array.Empty<RiskViolation>());
                riskManager.ValidateBudgetEntry(Arg.Any<decimal>(),
                    Arg.Any<EquitySnapshot>(), Arg.Any<decimal>())
                    .Returns(true);
                riskManager.InitialBalance.Returns(10_000m);
                riskManager.CurrentState.Returns(
                    new ExtendedRiskState
                    {
                        TradingAllowed = false, InProtectionMode = false,
                        DailyDrawdownUsed = 0m, MaxDrawdownUsed = 0m,
                    });
                // iter-36 K4: the kernel path reads these off the RiskManager (the imperative
                // Validate/CalculateLotSize above are dead now). Without them the evaluator NREs and
                // silently faults the worker, leaving the bar stream undrained (a hang).
                var replayRuleSet = new PropFirmRuleSet(
                    "ftmo-standard", "ftmo-standard", "Fixed", 0.05, 0.10, 0.10, 0,
                    "BalancePlusFloating", "22:00:00", "UTC", false, "High", 0, 0,
                    false, "21:00:00", "20:00:00", "NextTradingDay", false);
                riskManager.ActiveRuleSet.Returns(replayRuleSet);
                riskManager.Drawdown.Returns(TradingEngine.Engine.DrawdownReducer.CreateInitial(10_000m, "Fixed"));
                riskManager.CheckComplianceBlock(Arg.Any<TradeIntent>(), Arg.Any<RiskProfile>())
                    .Returns((string?)null);
                var governor = Substitute.For<ITradingGovernor>();
                governor.Evaluate(Arg.Any<GovernorContext>())
                    .Returns(new GovernorDecision(true, 1.0m, GovernorTradingState.Normal, "OK"));
                governor.GetSnapshot()
                    .Returns(new GovernorSnapshot(GovernorTradingState.Normal, 1.0m, 0, 0, 0, "OK"));

                services.AddSingleton<ITradingGovernor>(_ => governor);
                services.AddSingleton<IRiskManager>(_ => riskManager);

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
                // iter-36 K5: wire the single lossless StepRecord journal so the in-host replay produces
                // JournalEntries (the per-bar "why" + decisions now live here, not the deleted BarEvaluations).
                services.AddScoped<IStepRecordSink, SqliteStepRecordSink>();
                services.AddSingleton<TradingEngine.Domain.IJournalWriter>(sp =>
                    new TradingEngine.Engine.ChannelJournalWriter(
                        new ScopedStepRecordSink(sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>())));
                services.AddSingleton<PersistenceService>();

                services.AddSingleton<IEventBus, TypedEventBus>();
                services.AddSingleton<TradePersistenceHandler>();
                services.AddSingleton<EquityPersistenceHandler>();

                services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
                services.AddSingleton<IRegimeDetector>(_ => Substitute.For<IRegimeDetector>());

                var strategy = new AlwaysSignalStrategy();
                services.AddSingleton<IEnumerable<IStrategy>>(sp => [strategy]);

                var strategyBank = Substitute.For<IStrategyBank>();
                strategyBank.GetActive(Arg.Any<Symbol>(), Arg.Any<Timeframe>(), Arg.Any<MarketRegime>())
                    .Returns(new[] { strategy });
                services.AddSingleton<IStrategyBank>(_ => strategyBank);
                services.AddSingleton<ISignalGate, SignalGateService>();
                services.AddSingleton<IDecisionJournal>(_ => Substitute.For<IDecisionJournal>());
                services.AddSingleton<IEquitySink>(_ => Substitute.For<IEquitySink>());
                services.AddSingleton<OrderDispatcher>();
                services.AddSingleton<PositionTracker>();
                services.AddSingleton<IPositionManager, PositionManager>();

                // iter-36 K4: the kernel EngineRunner needs these (the old imperative wiring resolved them
                // elsewhere). Register the evaluator's external-verdict filters + the EffectExecutor it drives.
                services.AddSingleton<INewsFilter>(_ => Substitute.For<INewsFilter>());
                services.AddSingleton<SessionFilter>();
                services.AddSingleton<EngineRunContext>(_ => new EngineRunContext("replay-test"));
                services.AddSingleton<IProgress<BacktestProgressEvent>>(_ => new Progress<BacktestProgressEvent>(_ => { }));
                services.AddSingleton<TradingEngine.Services.EntryPlanner>();
                services.AddSingleton<IReadOnlyList<IStrategy>>(sp => sp.GetRequiredService<IEnumerable<IStrategy>>().ToList());
                services.AddSingleton<EffectExecutor>();

                services.AddSingleton<EngineWorkerDependencies>(sp => new EngineWorkerDependencies
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
                        RiskProfileResolver = sp.GetRequiredService<IRiskProfileResolver>(),
                        CrossRateProvider = sp.GetRequiredService<Func<string, string, decimal>>(),
                        Governor = sp.GetRequiredService<ITradingGovernor>(),
                        SizingPolicy = new SizingPolicyOptions(),
                        NewsFilter = sp.GetRequiredService<INewsFilter>(),
                        SessionFilter = sp.GetRequiredService<SessionFilter>(),
                    },
                    Strategies = new StrategyServices
                    {
                        Strategies = sp.GetRequiredService<IEnumerable<IStrategy>>(),
                        StrategyBank = sp.GetRequiredService<IStrategyBank>(),
                        RegimeDetector = sp.GetRequiredService<IRegimeDetector>(),
                        PositionTracker = sp.GetRequiredService<PositionTracker>(),
                        EntryPlanner = sp.GetRequiredService<TradingEngine.Services.EntryPlanner>(),
                        PositionManager = sp.GetRequiredService<IPositionManager>(),
                        SignalGate = sp.GetRequiredService<ISignalGate>(),
                    },
                    Persistence = new PersistenceServices
                    {
                        EventBus = sp.GetRequiredService<IEventBus>(),
                        Persistence = sp.GetRequiredService<PersistenceService>(),
                        EffectExecutor = sp.GetRequiredService<EffectExecutor>(),
                        Progress = null,
                        Journal = null,
                        StepJournal = sp.GetRequiredService<TradingEngine.Domain.IJournalWriter>(),
                    },
                });
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
        // RunAsync already stops the host in its finally; a second StopAsync on an already-stopped host
        // throws inside the generic host (Reverse(null)). Guard it so disposal is idempotent.
        try { await _host.StopAsync(); } catch { /* already stopped */ }
        _host.Dispose();

        for (var i = 0; i < 10 && File.Exists(_dbPath); i++)
        {
            try { File.Delete(_dbPath); break; }
            catch (IOException) { await Task.Delay(200); }
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.Events;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Risk;
using TradingEngine.Risk.Compliance;
using TradingEngine.Risk.Filters;
using TradingEngine.Risk.Governor;
using TradingEngine.Risk.Sizing;
using TradingEngine.Services;

namespace TradingEngine.Host;

public static class EngineHostFactory
{
    public static IHost Create(EngineHostOptions options)
    {
        var catalog = new SymbolCatalog(options.SolutionRoot);
        // Always load all symbols from config so strategies can resolve their configured
        // symbols regardless of which ones are selected for the backtest run.
        // SymbolNames controls which symbols get bars fed through the broker adapter.
        var symbols = catalog.GetAll();
        Console.WriteLine($"[EngineHostFactory] Loaded {symbols.Count} symbols from catalog (selected: {(options.SymbolNames.Count > 0 ? string.Join(",", options.SymbolNames) : "all")})");

        return Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l
                .SetMinimumLevel(options.MinLogLevel)
                .AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning))
            .ConfigureServices((ctx, services) =>
            {
                services.AddSingleton(new EngineRunContext(options.RunId));

                services.AddSingleton(options.AdapterFactory);

                var symbolRegistry = new SymbolInfoRegistry();
                foreach (var si in symbols)
                    symbolRegistry.Register(si);
                services.AddSingleton<ISymbolInfoRegistry>(_ => symbolRegistry);

                var crossRateStore = new CrossRateStore();
                services.AddSingleton(crossRateStore);
                services.AddSingleton<Func<string, string, decimal>>(_ => crossRateStore.Convert);

                services.AddSingleton<INewsFilter>(sp => new ConfigurableNewsFilter(
                    sp.GetRequiredService<LoadedConfig>().NewsWindows));
                services.AddSingleton<SessionFilter>();
                services.AddSingleton<DrawdownTracker>();
                services.AddSingleton<ICurrencyExposureTracker, CurrencyExposureTracker>();
                services.AddSingleton<RiskManager>();
                services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<RiskManager>());

                var loadedConfig = options.PreloadedConfig ?? new ConfigLoader(options.SolutionRoot).Load();
                services.AddSingleton(loadedConfig);

                services.AddSingleton(loadedConfig.SizingPolicy);
                services.AddSingleton(loadedConfig.Governor);
                services.AddSingleton<ITradingGovernor, TradingGovernorService>();
                services.AddSingleton<ISizeModifier, GovernorSizeModifier>();
                services.AddSingleton<IRiskProfileResolver>(sp =>
                    new RiskProfileResolver(
                        sp.GetRequiredService<LoadedConfig>().RiskProfiles));

                services.AddSingleton<IEngineClock, BrokerClock>();

                services.AddSingleton<IPassProbabilityEstimator, PassProbabilityEstimator>();

                services.AddSingleton<ISizeModifier, DrawdownSizeModifier>();
                services.AddSingleton<ISizeModifier, AtrRegimeSizeModifier>();
                services.AddSingleton<ISizeModifier, TimeOfDaySizeModifier>();
                services.AddSingleton<ISizeModifier, ConfidenceSizeModifier>();
                services.AddSingleton<SizeModifierPipeline>();

                services.AddDbContext<TradingDbContext>(o =>
                    o.UseSqlite($"Data Source={options.DbPath}"));
                services.AddScoped<ITradeRepository, SqliteTradeRepository>();
                services.AddScoped<IEquityRepository, SqliteEquityRepository>();
                services.AddScoped<IPipelineEventRepository, SqlitePipelineEventRepository>();
                services.AddSingleton<PersistenceService>();
                services.AddSingleton<PipelineEventWriter>(sp => new PipelineEventWriter(options.RunId,
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<ILogger<PipelineEventWriter>>()));
                services.AddSingleton<IPipelineJournal>(sp => sp.GetRequiredService<PipelineEventWriter>());
                services.AddSingleton<IDecisionJournal>(sp => sp.GetRequiredService<PipelineEventWriter>());

                services.AddSingleton<IPositionManager, PositionManager>();
                services.AddSingleton<IEventBus, TypedEventBus>();
                services.AddSingleton<EquityPersistenceHandler>();
                services.AddSingleton<TradePersistenceHandler>();
                services.AddSingleton<BarEvaluationHandler>();
                services.AddSingleton<ProtectionLedgerPersistenceHandler>();
                services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
                services.AddSingleton<IRegimeDetector, AtrBasedRegimeDetector>();
                services.AddSingleton<IStrategyBank>(sp => new StrategyBankService(
                    sp.GetRequiredService<StrategyRegistry>(),
                    sp.GetRequiredService<LoadedConfig>().StrategyRotation,
                    sp.GetRequiredService<ILogger<StrategyBankService>>()));
                services.AddSingleton<OrderDispatcher>();
                services.AddSingleton<PositionTracker>();
                services.AddSingleton<ISignalGate, SignalGateService>();

                if (options.Mode == EngineMode.Backtest)
                {
                    var buffered = new BufferedEquitySink();
                    services.AddSingleton<IEquitySink>(buffered);
                    services.AddSingleton<IAccountSnapshotStore>(buffered);
                }
                else
                {
                    services.AddSingleton<IEquitySink, PersistentEquitySink>();
                }

                if (options.Progress is not null)
                {
                    services.AddSingleton(options.Progress);
                }
                else
                {
                    services.AddSingleton<IProgress<BacktestProgressEvent>>(
                        _ => new Progress<BacktestProgressEvent>(_ => { }));
                }

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
                    new EngineWorkerDependencies
                    {
                        Market = new MarketServices
                        {
                            Broker = sp.GetRequiredService<IBrokerAdapter>(),
                            Indicators = sp.GetRequiredService<IIndicatorService>(),
                            SymbolRegistry = sp.GetRequiredService<ISymbolInfoRegistry>(),
                            CrossRateStore = sp.GetRequiredService<CrossRateStore>(),
                            Clock = sp.GetRequiredService<IEngineClock>(),
                            EngineMode = options.Mode,
                            DataFeed = null,
                        },
                        Risk = new RiskServices
                        {
                            RiskManager = sp.GetRequiredService<IRiskManager>(),
                            DrawdownTracker = sp.GetRequiredService<DrawdownTracker>(),
                            RiskProfileResolver = sp.GetRequiredService<IRiskProfileResolver>(),
                            CrossRateProvider = sp.GetRequiredService<Func<string, string, decimal>>(),
                            Governor = sp.GetRequiredService<ITradingGovernor>(),
                            SizingPolicy = sp.GetRequiredService<SizingPolicyOptions>(),
                        },
                        Strategies = new StrategyServices
                        {
                            Strategies = sp.GetRequiredService<IEnumerable<IStrategy>>(),
                            StrategyBank = sp.GetRequiredService<IStrategyBank>(),
                            RegimeDetector = sp.GetRequiredService<IRegimeDetector>(),
                            OrderDispatcher = sp.GetRequiredService<OrderDispatcher>(),
                            PositionTracker = sp.GetRequiredService<PositionTracker>(),
                            SignalGate = sp.GetRequiredService<ISignalGate>(),
                        },
                        Persistence = new PersistenceServices
                        {
                            EventBus = sp.GetRequiredService<IEventBus>(),
                            Persistence = sp.GetRequiredService<PersistenceService>(),
                            EquitySink = sp.GetService<IEquitySink>(),
                            Progress = sp.GetRequiredService<IProgress<BacktestProgressEvent>>(),
                            Journal = sp.GetRequiredService<IPipelineJournal>(),
                        },
                    },
                    sp.GetRequiredService<EngineRunContext>(),
                    sp.GetRequiredService<ILogger<EngineWorker>>()));
                services.AddHostedService<EngineWorker>(sp =>
                    sp.GetRequiredService<EngineWorker>());
            })
            .Build();
    }

    public static void WireEventHandlers(IHost host)
    {
        var eventBus = host.Services.GetRequiredService<IEventBus>();
        eventBus.Subscribe<EquityUpdated>(
            host.Services.GetRequiredService<EquityPersistenceHandler>());
        eventBus.Subscribe<TradeClosed>(
            host.Services.GetRequiredService<TradePersistenceHandler>());
        eventBus.Subscribe<BarEvaluated>(
            host.Services.GetRequiredService<BarEvaluationHandler>());
        eventBus.Subscribe<GovernorStateChanged>(
            host.Services.GetRequiredService<ProtectionLedgerPersistenceHandler>());
    }

    public static void WireRiskRules(IHost host)
    {
        var rm = host.Services.GetRequiredService<RiskManager>();
        var loaded = host.Services.GetRequiredService<LoadedConfig>();
        var activeRiskProfileId = loaded.StrategyConfigs
            .Select(c => c.RiskProfileId).FirstOrDefault() ?? "standard";
        var activeProfile = loaded.RiskProfiles.FirstOrDefault(r => r.Id == activeRiskProfileId);
        var activeRuleSetId = activeProfile?.PropFirmRuleSetId ?? "ftmo-standard";
        var ruleSet = loaded.PropFirms.FirstOrDefault(r => r.Id == activeRuleSetId);
        if (ruleSet is not null)
        {
            rm.SetActiveRuleSet(ruleSet);

            var passEstimator = host.Services.GetRequiredService<IPassProbabilityEstimator>();
            var complianceSvc = new PropFirmComplianceService(
                ruleSet,
                host.Services.GetRequiredService<DrawdownTracker>(),
                host.Services.GetRequiredService<IEngineClock>(),
                passEstimator);
            rm.SetComplianceService(complianceSvc);
        }

        var sizePipeline = host.Services.GetRequiredService<SizeModifierPipeline>();
        rm.SetSizePipeline(sizePipeline);
    }
}

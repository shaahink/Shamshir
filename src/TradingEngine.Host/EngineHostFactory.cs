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
using TradingEngine.Risk.Sizing;
using TradingEngine.Services;

namespace TradingEngine.Host;

public sealed record EngineHostOptions
{
    public required string RunId { get; init; }
    public required EngineMode Mode { get; init; }
    public required Func<IServiceProvider, IBrokerAdapter> AdapterFactory { get; init; }
    public required string DbPath { get; init; }
    public required string SolutionRoot { get; init; }
    public IReadOnlyList<string> SymbolNames { get; init; } = [];
    public IProgress<BacktestProgressEvent>? Progress { get; init; }
    public LogLevel MinLogLevel { get; init; } = LogLevel.Information;
}

public static class EngineHostFactory
{
    public static IHost Create(EngineHostOptions options)
    {
        var catalog = new SymbolCatalog(options.SolutionRoot);
        var symbols = options.SymbolNames.Count > 0
            ? catalog.ResolveAll(options.SymbolNames)
            : catalog.GetAll();

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

                services.AddSingleton<INewsFilter>(_ => new NewsFilter());
                services.AddSingleton<SessionFilter>();
                services.AddSingleton<DrawdownTracker>();
                services.AddSingleton<ICurrencyExposureTracker, CurrencyExposureTracker>();
                services.AddSingleton<RiskManager>();
                services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<RiskManager>());

                var configLoader = new ConfigLoader(options.SolutionRoot);
                var loadedConfig = configLoader.Load();
                services.AddSingleton(loadedConfig);
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

                services.AddSingleton<IPositionManager, PositionManager>();
                services.AddSingleton<IEventBus, TypedEventBus>();
                services.AddSingleton<EquityPersistenceHandler>();
                services.AddSingleton<TradePersistenceHandler>();
                services.AddSingleton<BarEvaluationHandler>();
                services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
                services.AddSingleton<OrderDispatcher>();
                services.AddSingleton<PositionTracker>();

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
                        },
                        Strategies = new StrategyServices
                        {
                            Strategies = sp.GetRequiredService<IEnumerable<IStrategy>>(),
                            OrderDispatcher = sp.GetRequiredService<OrderDispatcher>(),
                            PositionTracker = sp.GetRequiredService<PositionTracker>(),
                        },
                        Persistence = new PersistenceServices
                        {
                            EventBus = sp.GetRequiredService<IEventBus>(),
                            Persistence = sp.GetRequiredService<PersistenceService>(),
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

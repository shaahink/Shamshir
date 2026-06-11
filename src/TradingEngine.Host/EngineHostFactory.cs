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
using TradingEngine.Risk.Filters;
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
            .ConfigureLogging(l => l.SetMinimumLevel(options.MinLogLevel))
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
                services.AddSingleton<RiskManager>();
                services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<RiskManager>());

                var configLoader = new ConfigLoader(options.SolutionRoot);
                var loadedConfig = configLoader.Load();
                services.AddSingleton(loadedConfig);
                services.AddSingleton<IRiskProfileResolver>(sp =>
                    new RiskProfileResolver(
                        sp.GetRequiredService<LoadedConfig>().RiskProfiles));

                services.AddSingleton<IEngineClock, BrokerClock>();

                services.AddDbContext<TradingDbContext>(o =>
                    o.UseSqlite($"Data Source={options.DbPath}"));
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
                    options.Mode,
                    dataFeed: null,
                    progress: sp.GetRequiredService<IProgress<BacktestProgressEvent>>()));
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
            rm.SetActiveRuleSet(ruleSet);
    }
}

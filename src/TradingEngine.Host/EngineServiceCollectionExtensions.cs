using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.Caching;
using TradingEngine.Infrastructure.Events;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Infrastructure.Transport.NetMq;
using TradingEngine.Infrastructure.Venues.CTrader;
using TradingEngine.Infrastructure.Venues.Simulated;
using TradingEngine.Risk;
using TradingEngine.Risk.Compliance;
using TradingEngine.Risk.Filters;
using TradingEngine.Risk.Sizing;
using TradingEngine.Services;

namespace TradingEngine.Host;

public static class EngineServiceCollectionExtensions
{
    public static IServiceCollection AddEngineHost(this IServiceCollection services, EngineHostOptions options)
    {
        if (options.RunDataCache is not null)
            services.AddSingleton(options.RunDataCache);

        return services
            .AddMarketDataFromOptions(options)
            .AddRiskFromOptions(options)
            .AddPersistence(options.DbPath, options.SolutionRoot, options.SkipJournal)
            .AddStrategiesFromOptions(options)
            .AddEventInfrastructureFromOptions(options)
            .AddEngineWorkerFromOptions(options);
    }

    public static IServiceCollection AddPersistence(this IServiceCollection services, string dbPath)
    {
        return services.AddPersistence(dbPath, null);
    }

    public static IServiceCollection AddPersistence(this IServiceCollection services, string dbPath, string? basePath, bool skipJournal = false)
    {
        services.AddScoped<TradingEngine.Infrastructure.Persistence.AuditStampInterceptor>(sp =>
        {
            var clock = sp.GetService<TradingEngine.Domain.IEngineClock>();
            return clock is not null
                ? new TradingEngine.Infrastructure.Persistence.AuditStampInterceptor(clock)
                : new TradingEngine.Infrastructure.Persistence.AuditStampInterceptor();
        });
        services.AddSingleton<SqlitePragmaInterceptor>();
        services.AddDbContext<TradingDbContext>((sp, o) =>
        {
            o.UseSqlite($"Data Source={dbPath}");
            o.AddInterceptors(sp.GetRequiredService<TradingEngine.Infrastructure.Persistence.AuditStampInterceptor>());
            o.AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>());
        });
        services.AddScoped<ITradeRepository, SqliteTradeRepository>();
        services.AddScoped<IEquityRepository, SqliteEquityRepository>();
        services.AddScoped<IBarRepository, SqliteBarRepository>();
        services.AddScoped<IDatasetRepository, SqliteDatasetRepository>();
        services.AddScoped<IConfigSetRepository, SqliteConfigSetRepository>();
        services.AddScoped<IStepRecordSink, SqliteStepRecordSink>();
        services.AddScoped<IJournalQueryRepository, SqliteJournalQueryRepository>();
        // iter-36 K5: the single, lossless StepRecord journal the kernel engine writes to. Singleton per
        // (inner) host = per run; drains on host dispose. Scope-per-flush bridge to the scoped SQLite sink.
        // iter-tape-trust T5: SkipJournal disables per-bar StepRecord persistence for sweep runs
        // (trades + summary + equity are kept; only the per-bar narration is skipped).
        if (!skipJournal)
        {
            services.AddSingleton<IJournalWriter>(sp => new ChannelJournalWriter(
                new ScopedStepRecordSink(sp.GetRequiredService<IServiceScopeFactory>())));
        }
        else
        {
            services.AddSingleton<IJournalWriter, NullJournalWriter>();
        }
        services.AddScoped<IStrategyConfigStore, SqliteStrategyConfigStore>();
        services.AddScoped<IAddOnPackStore, SqliteAddOnPackStore>();   // iter-38 PK1
        services.AddSingleton<PersistenceService>();
        // iter-36 K5: the StepRecord journal (ScopedStepRecordSink + ChannelJournalWriter, above) is the
        // single journal writer. The old PipelineEventWriter/BarEvaluationHandler (Wait/DropOldest channels)
        // are deleted; the few legacy IDecisionJournal/IPipelineJournal consumers bind to no-ops.
        services.AddSingleton<IPipelineJournal, NullPipelineJournal>();
        services.AddSingleton<IDecisionJournal, NullDecisionJournal>();
        services.AddSingleton<IPositionManager, PositionManager>();
        services.AddSingleton<IEventBus, TypedEventBus>();
        services.AddSingleton<EquityPersistenceHandler>();
        services.AddSingleton<TradePersistenceHandler>();
        services.AddSingleton<BarPersistenceHandler>();
        services.AddSingleton<BufferedBarWriter>();

        if (basePath is not null)
        {
            services.AddSingleton<StrategyConfigSeeder>(sp => new StrategyConfigSeeder(
                sp.GetRequiredService<IServiceScopeFactory>(),
                basePath,
                sp.GetRequiredService<ILogger<StrategyConfigSeeder>>()));
        }

        return services;
    }

    private static IServiceCollection AddMarketDataFromOptions(this IServiceCollection services, EngineHostOptions options)
    {
        var catalog = new SymbolCatalog(options.SolutionRoot);
        var symbols = catalog.GetAll();
        services.AddSingleton(new EngineRunContext(options.RunId) { DiagnosticsEnabled = options.DiagnosticsEnabled });
        services.AddSingleton(options.AdapterFactory);

        var symbolRegistry = new SymbolInfoRegistry();
        foreach (var si in symbols) symbolRegistry.Register(si);
        services.AddSingleton<ISymbolInfoRegistry>(_ => symbolRegistry);

        var crossRateStore = new CrossRateStore();
        services.AddSingleton(crossRateStore);
        services.AddSingleton<Func<string, string, decimal>>(_ => crossRateStore.Convert);

        services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
        services.AddSingleton<IRegimeDetector, AtrBasedRegimeDetector>();
        services.AddSingleton<IEngineClock, BrokerClock>();
        return services;
    }

    private static IServiceCollection AddRiskFromOptions(this IServiceCollection services, EngineHostOptions options)
    {
        var loadedConfig = options.PreloadedConfig ?? new ConfigLoader(options.SolutionRoot).Load();
        services.AddSingleton(loadedConfig);
        services.AddSingleton<INewsFilter>(sp => new ConfigurableNewsFilter(loadedConfig.NewsWindows));
        services.AddSingleton<SessionFilter>();
        services.AddSingleton<ICurrencyExposureTracker, CurrencyExposureTracker>();
        services.AddSingleton<RiskManager>();
        services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<RiskManager>());
        services.AddSingleton(loadedConfig.SizingPolicy);
        services.AddSingleton(loadedConfig.Governor);
        services.AddSingleton(loadedConfig.Regime);
        services.AddSingleton<ITradingGovernor>(sp => new GovernorMachine(sp.GetRequiredService<GovernorOptions>()));
        services.AddSingleton<ISizeModifier, GovernorSizeModifier>();
        services.AddSingleton<IRiskProfileResolver>(sp => new RiskProfileResolver(loadedConfig.RiskProfiles));
        services.AddSingleton<IPassProbabilityEstimator, PassProbabilityEstimator>();
        services.AddSingleton<ISizeModifier, DrawdownSizeModifier>();
        services.AddSingleton<ISizeModifier, AtrRegimeSizeModifier>();
        services.AddSingleton<ISizeModifier, TimeOfDaySizeModifier>();
        services.AddSingleton<ISizeModifier, ConfidenceSizeModifier>();
        services.AddSingleton<SizeModifierPipeline>();
        return services;
    }

    private static IServiceCollection AddStrategiesFromOptions(this IServiceCollection services, EngineHostOptions options)
    {
        services.AddSingleton<IStrategyBank>(sp => new StrategyBankService(
            sp.GetRequiredService<StrategyRegistry>(),
            sp.GetRequiredService<LoadedConfig>().StrategyRotation,
            options.RunPlan,
            sp.GetRequiredService<ILogger<StrategyBankService>>()));
        services.AddSingleton<PositionTracker>();
        services.AddSingleton<EntryPlanner>();
        services.AddSingleton<EffectExecutor>();
        services.AddSingleton<IEffectExecutor>(sp => sp.GetRequiredService<EffectExecutor>());
        services.AddSingleton<ISignalGate, SignalGateService>();

        var registry = new StrategyRegistry();
        services.AddSingleton(registry);
        // Materialize the active strategies once and expose them under BOTH IReadOnlyList<IStrategy>
        // (EffectExecutor) and IEnumerable<IStrategy> (EngineWorkerDependencies). Registering only
        // IEnumerable left EffectExecutor unresolvable, which failed EngineHostFactory.Create at
        // startup (dashboard backtests + cTrader pipeline tests).
        services.AddSingleton<IReadOnlyList<IStrategy>>(sp =>
        {
            var reg = sp.GetRequiredService<StrategyRegistry>();
            var loaded = sp.GetRequiredService<LoadedConfig>();
            var runPlan = options.RunPlan ?? RunPlan.Empty;
            var activeIds = StrategyRegistry.SelectActiveIds(
                loaded.StrategyConfigs.Select(c => c.Id), options.ActiveStrategyIds);
            return reg.CreateStrategies(activeIds, loaded, runPlan, sp).ToList();
        });
        services.AddSingleton<IEnumerable<IStrategy>>(sp => sp.GetRequiredService<IReadOnlyList<IStrategy>>());
        return services;
    }

    private static IServiceCollection AddEventInfrastructureFromOptions(this IServiceCollection services, EngineHostOptions options)
    {
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
            services.AddSingleton(options.Progress);
        else
            services.AddSingleton<IProgress<BacktestProgressEvent>>(_ => new Progress<BacktestProgressEvent>(_ => { }));
        return services;
    }

    private static IServiceCollection AddEngineWorkerFromOptions(this IServiceCollection services, EngineHostOptions options)
    {
        services.AddSingleton<EngineWorkerDependencies>(sp => new EngineWorkerDependencies
        {
            Market = new MarketServices
            {
                Broker = sp.GetRequiredService<IBrokerAdapter>(),
                Indicators = sp.GetRequiredService<IIndicatorService>(),
                SymbolRegistry = sp.GetRequiredService<ISymbolInfoRegistry>(),
                CrossRateStore = sp.GetRequiredService<CrossRateStore>(),
                Clock = sp.GetRequiredService<IEngineClock>(),
                EngineMode = options.Mode,
                DataFeed = options.Mode == EngineMode.Backtest ? sp.GetService<DataFeedService>() : null,
            },
            Risk = new RiskServices
            {
                RiskManager = sp.GetRequiredService<IRiskManager>(),
                RiskProfileResolver = sp.GetRequiredService<IRiskProfileResolver>(),
                CrossRateProvider = sp.GetRequiredService<Func<string, string, decimal>>(),
                Governor = sp.GetRequiredService<ITradingGovernor>(),
                SizingPolicy = sp.GetRequiredService<SizingPolicyOptions>(),
                NewsFilter = sp.GetRequiredService<INewsFilter>(),
                SessionFilter = sp.GetRequiredService<SessionFilter>(),
            },
            Strategies = new StrategyServices
            {
                Strategies = sp.GetRequiredService<IEnumerable<IStrategy>>(),
                StrategyBank = sp.GetRequiredService<IStrategyBank>(),
                RegimeDetector = sp.GetRequiredService<IRegimeDetector>(),
                PositionTracker = sp.GetRequiredService<PositionTracker>(),
                EntryPlanner = sp.GetRequiredService<EntryPlanner>(),
                PositionManager = sp.GetRequiredService<IPositionManager>(),
                SignalGate = sp.GetRequiredService<ISignalGate>(),
            },
            Persistence = new PersistenceServices
            {
                EventBus = sp.GetRequiredService<IEventBus>(),
                Persistence = sp.GetRequiredService<PersistenceService>(),
                EffectExecutor = sp.GetRequiredService<EffectExecutor>(),
                EquitySink = sp.GetService<IEquitySink>(),
                Progress = sp.GetRequiredService<IProgress<BacktestProgressEvent>>(),
                Journal = sp.GetRequiredService<IPipelineJournal>(),
                StepJournal = sp.GetRequiredService<IJournalWriter>(),
                ScopeFactory = sp.GetRequiredService<IServiceScopeFactory>(),
                PreloadedAuxBars = options.PreloadedAuxBars,
            },
        });

        services.AddSingleton<EngineWorker>(sp => new EngineWorker(
            sp.GetRequiredService<EngineWorkerDependencies>(),
            sp.GetRequiredService<EngineRunContext>(),
            sp.GetRequiredService<ILogger<EngineWorker>>()));
        services.AddHostedService<EngineWorker>(sp => sp.GetRequiredService<EngineWorker>());
        return services;
    }

    private static ISymbolInfoRegistry LoadSymbolRegistry(string solutionRoot)
    {
        var reg = new SymbolInfoRegistry();
        var symbolsPath = Path.GetFullPath(Path.Combine(solutionRoot, "config", "symbols", "defaults.json"));
        if (File.Exists(symbolsPath))
        {
            var json = File.ReadAllText(symbolsPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var entries = JsonSerializer.Deserialize<List<SymbolJsonEntry>>(json, options);
            if (entries is not null)
            {
                foreach (var e in entries)
                {
                    reg.Register(new SymbolInfo(
                        Symbol.Parse(e.Symbol),
                        Enum.Parse<SymbolCategory>(e.Category),
                        e.BaseCurrency, e.QuoteCurrency,
                        (decimal)e.PipSize, (decimal)e.TickSize, (decimal)e.ContractSize,
                        (decimal)e.MinLots, (decimal)e.MaxLots, (decimal)e.LotStep,
                        (decimal)e.MarginRate, (decimal)e.TypicalSpread));
                }
            }
        }
        return reg;
    }

    private sealed record SymbolJsonEntry
    {
        public string Symbol { get; init; } = "";
        public string Category { get; init; } = "";
        public string BaseCurrency { get; init; } = "";
        public string QuoteCurrency { get; init; } = "";
        public double PipSize { get; init; }
        public double TickSize { get; init; }
        public double ContractSize { get; init; }
        public double MinLots { get; init; }
        public double MaxLots { get; init; }
        public double LotStep { get; init; }
        public double MarginRate { get; init; }
        public double TypicalSpread { get; init; }
    }
}

public static class EngineHostWireExtensions
{
    public static void WireEventHandlers(this IHost app)
    {
        var eventBus = app.Services.GetRequiredService<IEventBus>();
        eventBus.Subscribe<EquityUpdated>(app.Services.GetRequiredService<EquityPersistenceHandler>());
        eventBus.Subscribe<TradeClosed>(app.Services.GetRequiredService<TradePersistenceHandler>());
        eventBus.Subscribe<BarIngested>(app.Services.GetRequiredService<BarPersistenceHandler>());
    }

    public static void WireRiskRules(this IHost app, LoadedConfig loadedConfig)
    {
        var rm = app.Services.GetRequiredService<RiskManager>();
        var activeRiskProfileId = loadedConfig.StrategyConfigs
            .Select(c => c.RiskProfileId).FirstOrDefault() ?? "standard";
        var activeProfile = loadedConfig.RiskProfiles.FirstOrDefault(r => r.Id == activeRiskProfileId);
        var activeRuleSetId = activeProfile?.PropFirmRuleSetId ?? "ftmo-standard";
        var ruleSet = loadedConfig.PropFirms.FirstOrDefault(r => r.Id == activeRuleSetId);
        if (ruleSet is not null)
        {
            rm.SetActiveRuleSet(ruleSet);

            var resolvedProfile = activeProfile ?? new RiskProfile(
                "standard", "Standard", 1.0, 5.0, 10.0, 100.0, 10.0, 0.5, 0.1, 5,
                false, activeRuleSetId, LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);
            // iter-37 T8 / iter-38 B3: AND the Governor page's Enabled into the kernel gate switch,
            // same fix already applied in EngineHostFactory.WireRiskRules. Without this the governor
            // page's disable-toggle has no effect on the extension-method path.
            var constraints = ConstraintSet.Resolve(resolvedProfile, ruleSet);
            var govOptions = app.Services.GetRequiredService<GovernorOptions>();
            constraints = constraints with { GovernorEnabled = constraints.GovernorEnabled && govOptions.Enabled };
            rm.SetConstraints(constraints);
            var passEstimator = app.Services.GetRequiredService<IPassProbabilityEstimator>();
            var complianceSvc = new PropFirmComplianceService(
                ruleSet, rm, app.Services.GetRequiredService<IEngineClock>(), passEstimator);
            rm.SetComplianceService(complianceSvc);
        }

        var sizePipeline = app.Services.GetRequiredService<SizeModifierPipeline>();
        rm.SetSizePipeline(sizePipeline);
    }
}

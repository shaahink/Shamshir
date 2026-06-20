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
        return services
            .AddMarketDataFromOptions(options)
            .AddRiskFromOptions(options)
            .AddPersistence(options.DbPath, options.SolutionRoot)
            .AddStrategiesFromOptions(options)
            .AddEventInfrastructureFromOptions(options)
            .AddEngineWorkerFromOptions(options);
    }

    // -- Mode-aware overloads (used by Program.cs) --

    public static IServiceCollection AddMarketData(this IServiceCollection services, EngineMode mode, string solutionRoot, double slipPips, IConfiguration config)
    {
        services.AddSingleton(new EngineRunContext(config["Engine:RunId"] ?? ""));

        if (mode == EngineMode.Live || mode == EngineMode.Paper)
        {
            var dataPort = int.TryParse(config["Engine:Broker:NetMQ:DataPort"], out var dp) ? dp : 15555;
            var commandPort = int.TryParse(config["Engine:Broker:NetMQ:CommandPort"], out var cp) ? cp : 15556;
            services.AddSingleton<IBrokerAdapter>(sp =>
            {
                var transport = new NetMqMessageTransport(
                    $"tcp://127.0.0.1:{dataPort}", $"tcp://*:{commandPort}",
                    sp.GetRequiredService<ILogger<NetMqMessageTransport>>());
                return new CTraderBrokerAdapter(transport,
                    sp.GetRequiredService<ILogger<CTraderBrokerAdapter>>());
            });
        }
        else
        {
            services.AddSingleton<IBrokerAdapter>(sp =>
                new SimulatedBrokerAdapter(
                    sp.GetRequiredService<ISymbolInfoRegistry>(),
                    sp.GetRequiredService<Func<string, string, decimal>>(),
                    slippagePips: slipPips));
        }

        if (mode == EngineMode.Backtest)
        {
            var dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tests", "data"));
            services.AddSingleton<IMarketDataProvider>(_ => new HistoricalDataProvider(dataDir));
        }
        else
        {
            services.AddSingleton<IMarketDataProvider, LiveMarketDataProvider>();
        }

        services.AddSingleton<ISymbolInfoRegistry>(sp => LoadSymbolRegistry(solutionRoot));

        var crossRateStore = new CrossRateStore();
        services.AddSingleton(crossRateStore);
        services.AddSingleton<Func<string, string, decimal>>(_ => crossRateStore.Convert);

        services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
        services.AddSingleton<IRegimeDetector, AtrBasedRegimeDetector>();
        services.AddSingleton<IEngineClock, BrokerClock>();

        return services;
    }

    public static IServiceCollection AddRisk(this IServiceCollection services, string solutionRoot)
    {
        var loadedConfig = new ConfigLoader(solutionRoot).LoadBase();
        services.AddSingleton(loadedConfig);

        services.AddSingleton<INewsFilter>(sp =>
            new ConfigurableNewsFilter(loadedConfig.NewsWindows));
        services.AddSingleton<SessionFilter>();
        services.AddSingleton<ICurrencyExposureTracker, CurrencyExposureTracker>();
        services.AddSingleton<RiskManager>();
        services.AddSingleton<IRiskManager>(sp => sp.GetRequiredService<RiskManager>());

        services.AddSingleton(loadedConfig.SizingPolicy);
        services.AddSingleton(loadedConfig.Governor);
        services.AddSingleton(loadedConfig.Regime);
        services.AddSingleton<ITradingGovernor>(sp => new GovernorMachine(sp.GetRequiredService<GovernorOptions>()));
        services.AddSingleton<ISizeModifier, GovernorSizeModifier>();
        services.AddSingleton<IRiskProfileResolver>(sp =>
            new RiskProfileResolver(loadedConfig.RiskProfiles));
        services.AddSingleton<IPassProbabilityEstimator, PassProbabilityEstimator>();
        services.AddSingleton<ISizeModifier, DrawdownSizeModifier>();
        services.AddSingleton<ISizeModifier, AtrRegimeSizeModifier>();
        services.AddSingleton<ISizeModifier, TimeOfDaySizeModifier>();
        services.AddSingleton<ISizeModifier, ConfidenceSizeModifier>();
        services.AddSingleton<SizeModifierPipeline>();

        return services;
    }

    public static IServiceCollection AddPersistence(this IServiceCollection services, string dbPath)
    {
        return services.AddPersistence(dbPath, null);
    }

    public static IServiceCollection AddPersistence(this IServiceCollection services, string dbPath, string? basePath)
    {
        services.AddDbContext<TradingDbContext>(o =>
            o.UseSqlite($"Data Source={dbPath}"));
        services.AddScoped<ITradeRepository, SqliteTradeRepository>();
        services.AddScoped<IEquityRepository, SqliteEquityRepository>();
        services.AddScoped<IPipelineEventRepository, SqlitePipelineEventRepository>();
        services.AddScoped<IBarRepository, SqliteBarRepository>();
        services.AddScoped<IDatasetRepository, SqliteDatasetRepository>();
        services.AddScoped<IConfigSetRepository, SqliteConfigSetRepository>();
        services.AddScoped<IStepRecordSink, SqliteStepRecordSink>();
        services.AddScoped<IJournalQueryRepository, SqliteJournalQueryRepository>();
        // iter-36 K5: the single, lossless StepRecord journal the kernel engine writes to. Singleton per
        // (inner) host = per run; drains on host dispose. Scope-per-flush bridge to the scoped SQLite sink.
        services.AddSingleton<IJournalWriter>(sp => new ChannelJournalWriter(
            new ScopedStepRecordSink(sp.GetRequiredService<IServiceScopeFactory>())));
        services.AddScoped<IStrategyConfigStore, SqliteStrategyConfigStore>();
        services.AddSingleton<PersistenceService>();
        services.AddSingleton<PipelineEventWriter>(sp => new PipelineEventWriter(
            sp.GetRequiredService<EngineRunContext>().RunId,
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

    public static IServiceCollection AddStrategies(this IServiceCollection services)
    {
        services.AddSingleton<IStrategyBank>(sp => new StrategyBankService(
            sp.GetRequiredService<StrategyRegistry>(),
            sp.GetRequiredService<LoadedConfig>().StrategyRotation,
            sp.GetService<RunPlan>(),
            sp.GetRequiredService<ILogger<StrategyBankService>>()));
        services.AddSingleton<OrderDispatcher>();
        services.AddSingleton<KernelOrderGate>(); // iter-35 AF2: the kernel gate is now the production authority
        services.AddSingleton<PositionTracker>();
        services.AddSingleton<EntryPlanner>();
        services.AddSingleton<EffectExecutor>();
        services.AddSingleton<IEffectExecutor>(sp => sp.GetRequiredService<EffectExecutor>());
        services.AddSingleton<ISignalGate, SignalGateService>();

        var registry = new StrategyRegistry();
        services.AddSingleton(registry);
        // Expose under both IReadOnlyList<IStrategy> (EffectExecutor) and IEnumerable<IStrategy>
        // (EngineWorkerDependencies) — see AddStrategiesFromOptions for the same fix.
        services.AddSingleton<IReadOnlyList<IStrategy>>(sp =>
        {
            var reg = sp.GetRequiredService<StrategyRegistry>();
            var loaded = sp.GetRequiredService<LoadedConfig>();
            var config = sp.GetRequiredService<IConfiguration>();
            var activeIds = config.GetValue<string>("Engine:ActiveStrategyIds")
                ?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                ?? loaded.StrategyConfigs.Select(c => c.Id).ToArray();
            return reg.CreateStrategies(activeIds, loaded, sp).ToList();
        });
        services.AddSingleton<IEnumerable<IStrategy>>(sp => sp.GetRequiredService<IReadOnlyList<IStrategy>>());

        return services;
    }

    public static IServiceCollection AddEventInfrastructure(this IServiceCollection services, EngineMode mode)
    {
        if (mode == EngineMode.Backtest)
        {
            var buffered = new BufferedEquitySink();
            services.AddSingleton<IEquitySink>(buffered);
            services.AddSingleton<IAccountSnapshotStore>(buffered);
        }
        else
        {
            services.AddSingleton<IEquitySink, PersistentEquitySink>();
        }

        services.AddSingleton<IProgress<BacktestProgressEvent>>(_ =>
            new Progress<BacktestProgressEvent>(_ => { }));

        return services;
    }

    public static IServiceCollection AddEngineWorker(this IServiceCollection services, EngineMode mode)
    {
        if (mode == EngineMode.Backtest)
        {
            var loadedConfig = services.BuildServiceProvider().GetRequiredService<LoadedConfig>();
            services.AddSingleton<DataFeedService>(sp =>
            {
                var symbols = loadedConfig.StrategyConfigs
                    .SelectMany(c => c.Symbols).Distinct().Select(Symbol.Parse).ToList();
                return new DataFeedService(
                    sp.GetRequiredService<IMarketDataProvider>(),
                    sp.GetRequiredService<IBrokerAdapter>(),
                    sp.GetRequiredService<ILogger<DataFeedService>>())
                { Symbols = symbols.Count > 0 ? symbols : [Symbol.Parse("EURUSD")] };
            });
            services.AddHostedService<DataFeedService>(sp => sp.GetRequiredService<DataFeedService>());
        }

        services.AddSingleton<EngineWorkerDependencies>(sp => new EngineWorkerDependencies
        {
            Market = new MarketServices
            {
                Broker = sp.GetRequiredService<IBrokerAdapter>(),
                Indicators = sp.GetRequiredService<IIndicatorService>(),
                SymbolRegistry = sp.GetRequiredService<ISymbolInfoRegistry>(),
                CrossRateStore = sp.GetRequiredService<CrossRateStore>(),
                Clock = sp.GetRequiredService<IEngineClock>(),
                EngineMode = mode,
                DataFeed = mode == EngineMode.Backtest ? sp.GetService<DataFeedService>() : null,
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
                OrderGate = sp.GetRequiredService<KernelOrderGate>(),
                PositionTracker = sp.GetRequiredService<PositionTracker>(),
                EntryPlanner = sp.GetRequiredService<EntryPlanner>(),
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
            },
        });

        services.AddSingleton<EngineWorker>(sp => new EngineWorker(
            sp.GetRequiredService<EngineWorkerDependencies>(),
            sp.GetRequiredService<EngineRunContext>(),
            sp.GetRequiredService<ILogger<EngineWorker>>()));
        services.AddHostedService<EngineWorker>(sp => sp.GetRequiredService<EngineWorker>());

        return services;
    }

    // -- Original EngineHostOptions-based overloads (backward compat) --

    private static IServiceCollection AddMarketDataFromOptions(this IServiceCollection services, EngineHostOptions options)
    {
        var catalog = new SymbolCatalog(options.SolutionRoot);
        var symbols = catalog.GetAll();
        services.AddSingleton(new EngineRunContext(options.RunId));
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
        services.AddSingleton<OrderDispatcher>();
        services.AddSingleton<KernelOrderGate>(); // iter-35 AF2: the kernel gate is now the production authority
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
            // Honour the run's strategy selection (the New-Backtest picker). Empty = all configured.
            var activeIds = StrategyRegistry.SelectActiveIds(
                loaded.StrategyConfigs.Select(c => c.Id), options.ActiveStrategyIds);
            return reg.CreateStrategies(activeIds, loaded, sp).ToList();
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
                OrderGate = sp.GetRequiredService<KernelOrderGate>(),
                PositionTracker = sp.GetRequiredService<PositionTracker>(),
                EntryPlanner = sp.GetRequiredService<EntryPlanner>(),
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
        eventBus.Subscribe<BarEvaluated>(app.Services.GetRequiredService<BarEvaluationHandler>());
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
            rm.SetConstraints(ConstraintSet.Resolve(resolvedProfile, ruleSet));
            var passEstimator = app.Services.GetRequiredService<IPassProbabilityEstimator>();
            var complianceSvc = new PropFirmComplianceService(
                ruleSet, rm, app.Services.GetRequiredService<IEngineClock>(), passEstimator);
            rm.SetComplianceService(complianceSvc);
        }

        var sizePipeline = app.Services.GetRequiredService<SizeModifierPipeline>();
        rm.SetSizePipeline(sizePipeline);
    }
}

using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;
using TradingEngine.Domain.Interfaces;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Infrastructure.MarketData;
using TradingEngine.Infrastructure.MarketData.Sync;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Infrastructure.Persistence.Reporting;
using TradingEngine.Risk.Compliance;
using TradingEngine.Services;
using TradingEngine.Web.Services;

namespace TradingEngine.Web.Configuration;

public static class ServiceRegistration
{
    public static IServiceCollection AddShamshir(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<CTraderConnectionOptions>(
            config.GetSection(CTraderConnectionOptions.Section));

        services.AddApi();
        services.AddPersistence(config);
        services.AddAppServices();
        services.AddEngineServices();

        services.AddHostedService<CacheEvictionSweeper>();

        if (config.GetValue<bool>("Dev:NgServe", false))
            services.AddHostedService<NgServeHost>();

        return services;
    }

    private static IServiceCollection AddApi(this IServiceCollection services)
    {
        services.AddControllers()
            .AddJsonOptions(o =>
            {
                o.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
                o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                // iter-38 W-B8: emit every DateTime as UTC 'Z' so the browser doesn't reinterpret it as local.
                o.JsonSerializerOptions.Converters.Add(new TradingEngine.Web.Configuration.Json.UtcDateTimeConverter());
            });
        services.AddSignalR()
            .AddJsonProtocol(o =>
            {
                o.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                // iter-38 W-B8: same UTC-'Z' DateTime contract on the live-monitor payloads.
                o.PayloadSerializerOptions.Converters.Add(new TradingEngine.Web.Configuration.Json.UtcDateTimeConverter());
            });
        services.AddSingleton<RunProgressBroadcaster>();
        services.AddOpenApi();
        services.AddCors(o => o.AddDefaultPolicy(p =>
            p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
        return services;
    }

    private static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration config)
    {
        // F10 (P1.1): one DB path, resolved from the repo root (cwd-independent) and shared with the
        // Host CLI + backtest orchestrator via DbPathResolver — no more "two databases" split.
        var dbPath = DbPathResolver.ResolveTradingDbPath(config.GetValue<string>("Persistence:DbPath"));
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var cs = $"Data Source={dbPath}";

        services.AddScoped<AuditStampInterceptor>(sp =>
        {
            var clock = sp.GetService<TradingEngine.Domain.IEngineClock>();
            return clock is not null
                ? new AuditStampInterceptor(clock)
                : new AuditStampInterceptor();
        });
        services.AddSingleton<SqlitePragmaInterceptor>();
        services.AddDbContext<TradingDbContext>((sp, o) =>
        {
            o.UseSqlite(cs, sqlOpts => sqlOpts.CommandTimeout(30));
            o.AddInterceptors(sp.GetRequiredService<AuditStampInterceptor>());
            o.AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>());
        });
        services.AddDbContext<ReportingDbContext>(o => o.UseSqlite(cs, sqlOpts =>
        {
            sqlOpts.CommandTimeout(30);
        }));

        // H21: enable WAL mode (persistent in the DB file header; every subsequent connection inherits it).
        // busy_timeout and perf PRAGMAs are now applied per-connection by SqlitePragmaInterceptor.
        var initCs = $"Data Source={dbPath};Mode=ReadWriteCreate";
        using var initConn = new Microsoft.Data.Sqlite.SqliteConnection(initCs);
        initConn.Open();
        using var walCmd = initConn.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        walCmd.ExecuteNonQuery();
        initConn.Close();

        // iter-marketdata-tape P1: canonical market-data store in its OWN SQLite file, so long-lived shared
        // history and churny per-run data never share a write lock or lifecycle (PLAN §5 / D1).
        var mdPath = DbPathResolver.ResolveMarketDataDbPath(
            config.GetValue<string>("MarketData:DbPath"), dbPath);
        var mdCs = $"Data Source={mdPath}";
        services.AddDbContextFactory<MarketDataDbContext>((sp, o) =>
        {
            o.UseSqlite(mdCs);
            o.AddInterceptors(sp.GetRequiredService<SqlitePragmaInterceptor>());
        });
        services.AddSingleton<SqliteMarketDataStore>();
        services.AddSingleton<IMarketDataStore>(sp => new BootstrapMarketDataStore(sp.GetRequiredService<SqliteMarketDataStore>()));
        services.AddSingleton<MarketDataSyncStore>();
        services.AddSingleton<MarketDataCoverageService>();
        // Create the file + schema once; WAL persists in the file header for every later connection.
        using (var mdInit = new MarketDataDbContext(
            new DbContextOptionsBuilder<MarketDataDbContext>().UseSqlite(mdCs).Options))
        {
            mdInit.Database.EnsureCreated();
            SqliteMarketDataStore.EnsureSpreadColumnAsync(mdInit).GetAwaiter().GetResult();
            // X4: idempotently add the auto-sync watchlist table on an existing (multi-GB) DB.
            MarketDataSyncStore.EnsureSchemaAsync(mdInit).GetAwaiter().GetResult();
        }
        using (var mdWal = new SqliteConnection($"Data Source={mdPath};Mode=ReadWriteCreate"))
        {
            mdWal.Open();
            using var mdWalCmd = mdWal.CreateCommand();
            mdWalCmd.CommandText = "PRAGMA journal_mode=WAL;";
            mdWalCmd.ExecuteNonQuery();
        }

        services.AddScoped<IBacktestRunRepository, SqliteBacktestRunRepository>();
        services.AddScoped<IJournalQueryRepository, SqliteJournalQueryRepository>();
        services.AddScoped<IBarRepository, SqliteBarRepository>();
        services.AddScoped(_ => new TradeReportQueries(new SqliteConnection(cs)));
        services.AddScoped<IExperimentRepository, SqliteExperimentRepository>();
        services.AddScoped<ITradeRepository, SqliteTradeRepository>();
        services.AddScoped<IEquityRepository, SqliteEquityRepository>();
        services.AddScoped<IExcursionRepository, SqliteExcursionRepository>();
        services.AddScoped<IExitCalibrationLookup, SqliteExitCalibrationLookup>();
        services.AddMemoryCache();
        services.AddScoped<IStrategyConfigStore, SqliteStrategyConfigStore>();
        services.AddScoped<IRiskProfileStore, SqliteRiskProfileStore>();
        services.AddScoped<IPropFirmRuleSetStore, SqlitePropFirmRuleSetStore>();
        services.AddScoped<IGovernorOptionsStore, SqliteGovernorOptionsStore>();
        services.AddScoped<IAddOnPackStore, SqliteAddOnPackStore>();   // iter-38 PK1
        // P0.3 (F6): the trade-persistence integrity barrier — reconciles journalled closes vs persisted
        // TradeResults at finalization and backfills any lost trades from the journal.
        services.AddScoped<TradingEngine.Infrastructure.Persistence.TradePersistenceBarrier>();
        return services;
    }

    private static IServiceCollection AddAppServices(this IServiceCollection services)
    {
        // Open trap #5: the Web orchestrator hosts have no broker clock — register a plain
        // SystemClock so controllers never call DateTime.UtcNow directly.
        services.AddSingleton<IEngineClock, SystemClock>();
        services.AddScoped<IRunQueryService, RunQueryService>();
        services.AddScoped<IBarQueryService, BarQueryService>();

        services.AddSingleton<IExperimentHostFactory, ExperimentHostFactoryAdapter>();
        services.AddTransient<ExperimentRunner>();
        services.AddSingleton<BacktestProgressStore>();
        services.AddSingleton<BacktestJournal>();
        services.AddSingleton<RunProjection>();
        services.AddSingleton<EffectiveConfigResolver>();
        // X4: single owner of every cTrader-cli invocation (backtest + download) — dynamic ports,
        // one shared bounded lane, owned-PID reaping. Bound from CTrader:ProcessOwner (default max 2).
        services.AddOptions<CTraderProcessOwnerOptions>().BindConfiguration("CTrader:ProcessOwner");
        services.AddSingleton<CTraderProcessOwner>();
        services.AddSingleton<RunRegistry>();
        services.AddSingleton<IVenueRunner, ReplayVenueRunner>();
        services.AddSingleton<IVenueRunner, CTraderVenueRunner>();
        services.AddSingleton<VenueRunnerRegistry>();
        services.AddSingleton<RunConfigAssembler>();
        services.AddSingleton<RunRecordStore>();
        services.AddSingleton<RunMarketContextLoader>();
        services.AddSingleton<BacktestOrchestrator>();
        services.AddSingleton<IBacktestCommandService>(sp => sp.GetRequiredService<BacktestOrchestrator>());
        // The read side (RunQueryService and the Runs/ query classes) observes live runs through
        // this narrow port instead of taking the command-side orchestrator class.
        services.AddSingleton<ILiveRunReader>(sp => sp.GetRequiredService<BacktestOrchestrator>());
        services.AddSingleton<DownloadJobService>();
        // X4: on-by-default background auto-sync loop (inert until the watchlist has cells).
        services.AddOptions<AutoSyncOptions>().BindConfiguration("MarketData:AutoSync");
        services.AddSingleton<AutoSyncService>();
        services.AddHostedService(sp => sp.GetRequiredService<AutoSyncService>());
        services.AddSingleton<ReferenceScalePopulator>();
        services.AddSingleton<DataQualityValidator>();
        // P1.2 (F9): propagate config/strategies + config/risk-profiles JSON edits into the DB on startup
        // (and expose GET /api/system/config-drift), without clobbering UI hand-edits.
        services.AddScoped(sp => new TradingEngine.Infrastructure.Configuration.ConfigSyncService(
            sp.GetRequiredService<TradingDbContext>(),
            sp.GetRequiredService<IStrategyConfigStore>(),
            sp.GetRequiredService<IRiskProfileStore>(),
            DbPathResolver.FindRepoRoot(),
            sp.GetRequiredService<ILogger<TradingEngine.Infrastructure.Configuration.ConfigSyncService>>()));
        services.AddScoped<IReferenceScaleLookup, SqliteReferenceScaleLookup>();
        services.AddSingleton<SweepRunnerService>();
        services.AddScoped<Services.LedgerReconcileService>();
        services.AddScoped<Services.ParityGateService>();
        services.AddScoped<Services.RunNarrativeService>();
        services.AddScoped<Services.PassProbabilityService>();
        services.AddScoped<Services.ChallengeSimulationService>();
        services.AddScoped<Services.SetupScoreService>();
        services.AddSingleton<WalkForwardBackgroundService>();
        services.AddHostedService(sp => sp.GetRequiredService<WalkForwardBackgroundService>());
        services.AddSingleton<IBacktestQueryService, BacktestQueryService>();
        services.AddSingleton<CTraderListenService>();
        services.AddSingleton<IRunDataCache, TradingEngine.Infrastructure.Caching.RunDataCache>();
        return services;
    }

    private static IServiceCollection AddEngineServices(this IServiceCollection services)
    {
        services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
        services.AddSingleton<IRegimeDetector, AtrBasedRegimeDetector>();
        services.AddSingleton<IPassProbabilityEstimator, PassProbabilityEstimator>();
        // P0.3 (F6): the root container needs a cross-rate provider for the TradePersistenceBarrier's
        // journal backfill (TradeResultFactory recomputes PnL currency). The per-run engine host has its
        // own live CrossRateStore; this root-level one uses the seeded defaults, sufficient for the
        // currency-tagging the backfill needs (venue trades carry their own gross/net PnL).
        services.AddSingleton<TradingEngine.Application.CrossRateStore>();
        services.AddSingleton<Func<string, string, decimal>>(sp =>
            sp.GetRequiredService<TradingEngine.Application.CrossRateStore>().Convert);
        services.AddSingleton<IVenueSymbolSpecStore, SqliteVenueSymbolSpecStore>();
        services.AddSingleton<ISymbolInfoRegistry>(sp =>
        {
            var solRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var accountCurrency = sp.GetRequiredService<IConfiguration>()
                .GetValue<string>("Account:Currency") is { Length: > 0 } c ? c.ToUpperInvariant() : "USD";
            var catalog = new SymbolCatalog(solRoot, accountCurrency);
            var reg = new SymbolInfoRegistry();
            foreach (var si in catalog.GetAll()) reg.Register(si);

            // P4.4 (F44): overlay the venue's OWN economics on top of the symbols.json defaults. Only the
            // cTrader leg ever meets a cBot, so without this the TAPE leg prices commission and swap off
            // symbols.json — which is fabricated (it has a EURUSD long EARNING 0.5/lot/night where the
            // broker CHARGES 7.04). Persisted specs make the venue's numbers survive the process that
            // learned them, so both legs of a compare-both are priced off the same source (D10).
            try
            {
                var specs = sp.GetRequiredService<IVenueSymbolSpecStore>()
                    .LoadAllAsync().GetAwaiter().GetResult();
                foreach (var spec in specs) reg.UpsertVenueSpec(spec);

                if (specs.Count > 0)
                {
                    sp.GetRequiredService<ILogger<SymbolInfoRegistry>>().LogInformation(
                        "VENUE_SPECS_LOADED {Count} venue symbol spec(s) — commission/swap sourced from the broker, not symbols.json",
                        specs.Count);
                }
            }
            catch (Exception ex)
            {
                // A missing/locked spec table must not stop the app booting — the registry still has the
                // symbols.json fallback, and SymbolInfoRegistry.Register already warns loudly (SYMBOL_FALLBACK).
                sp.GetRequiredService<ILogger<SymbolInfoRegistry>>().LogWarning(ex,
                    "Could not load persisted venue symbol specs — falling back to symbols.json economics");
            }

            return reg;
        });

        var registry = new StrategyRegistry();
        services.AddSingleton(registry);

        services.AddSingleton<IReadOnlyList<IStrategy>>(sp =>
        {
            var reg = sp.GetRequiredService<StrategyRegistry>();
            var store = sp.GetRequiredService<IStrategyConfigStore>();
            var configs = store.GetAllAsync(default).GetAwaiter().GetResult();

            var loaded = new LoadedConfig([], [])
            {
                StrategyConfigs = configs.Select(c => new StrategyConfigEntry(
                    c.Id, c.DisplayName, c.Enabled, c.RiskProfileId,
                    c.Parameters)
                {
                    RegimeFilter = c.RegimeFilter,
                    OrderEntry = c.OrderEntry,
                    PositionManagement = c.PositionManagement,
                    Reentry = c.Reentry,
                }).ToList(),
            };

            var activeIds = configs.Where(c => c.Enabled).Select(c => c.Id).ToList();
            if (activeIds.Count == 0 && configs.Count > 0)
                activeIds = [configs[0].Id];
            return reg.CreateStrategies(activeIds, loaded, RunPlan.Empty, sp).ToList();
        });
        services.AddSingleton<IEnumerable<IStrategy>>(sp => sp.GetRequiredService<IReadOnlyList<IStrategy>>());

        services.AddSingleton<IStrategyBank>(sp => new StrategyBankService(
            sp.GetRequiredService<StrategyRegistry>(), null, null,
            sp.GetRequiredService<ILogger<StrategyBankService>>()));
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        services.AddSingleton(new ConfigLoader(solutionRoot).LoadBase().Governor);
        services.AddSingleton<ITradingGovernor>(sp => new GovernorMachine(sp.GetRequiredService<GovernorOptions>()));
        services.AddSingleton(new RegimeOptions());
        return services;
    }
}

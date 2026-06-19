using Microsoft.Data.Sqlite;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Indicators;
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
        services.AddControllers();
        services.AddSignalR()
            .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy =
                System.Text.Json.JsonNamingPolicy.CamelCase);
        services.AddSingleton<RunProgressBroadcaster>();
        services.AddOpenApi();

        services.AddCors(o => o.AddDefaultPolicy(p =>
        {
            p.WithOrigins("http://localhost:4200")
             .AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        }));

        var dbPath = config.GetValue<string>("Persistence:DbPath")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "trading.db"));
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var cs = $"Data Source={dbPath}";
        services.AddDbContext<TradingDbContext>(o => o.UseSqlite(cs));
        services.AddDbContext<ReportingDbContext>(o => o.UseSqlite(cs));

        services.AddScoped<IBacktestRunRepository, SqliteBacktestRunRepository>();
        services.AddScoped<IBarRepository, SqliteBarRepository>();
        services.AddScoped(_ => new TradeReportQueries(new SqliteConnection(cs)));
        services.AddScoped<IPipelineEventRepository, SqlitePipelineEventRepository>();
        services.AddScoped<IExperimentRepository, SqliteExperimentRepository>();
        services.AddScoped<ITradeRepository, SqliteTradeRepository>();
        services.AddScoped<IEquityRepository, SqliteEquityRepository>();
        services.AddScoped<IStrategyConfigStore, SqliteStrategyConfigStore>();

        services.AddScoped<IRunQueryService, RunQueryService>();
        services.AddScoped<IProtectionQueryService, ProtectionQueryService>();
        services.AddScoped<IBarQueryService, BarQueryService>();

        services.AddSingleton<IExperimentHostFactory, ExperimentHostFactoryAdapter>();
        services.AddTransient<ExperimentRunner>();
        services.AddSingleton<BacktestProgressStore>();
        services.AddSingleton<BacktestJournal>();
        services.AddSingleton<RunProjection>();
        services.AddSingleton<EffectiveConfigResolver>();
        services.AddSingleton<BacktestOrchestrator>();
        services.AddSingleton<IBacktestCommandService>(sp => sp.GetRequiredService<BacktestOrchestrator>());
        services.AddSingleton<IBacktestQueryService, BacktestQueryService>();

        services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
        services.AddSingleton<IRegimeDetector, AtrBasedRegimeDetector>();
        services.AddSingleton<IPassProbabilityEstimator, PassProbabilityEstimator>();
        services.AddSingleton<ISymbolInfoRegistry>(_ =>
        {
            var solRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var catalog = new SymbolCatalog(solRoot);
            var reg = new SymbolInfoRegistry();
            foreach (var si in catalog.GetAll()) reg.Register(si);
            return reg;
        });
        services.AddSingleton<StrategyRegistry>();
        services.AddSingleton<IStrategyBank>(sp => new StrategyBankService(
            sp.GetRequiredService<StrategyRegistry>(), null, null,
            sp.GetRequiredService<ILogger<StrategyBankService>>()));
        services.AddSingleton<ITradingGovernor, TradingGovernorService>();
        services.AddSingleton(new GovernorOptions());
        services.AddSingleton(new RegimeOptions());
        services.AddSingleton<ProtectionLedgerWriter>();

        if (config.GetValue<bool>("Dev:NgServe", false))
            services.AddHostedService<NgServeHost>();

        return services;
    }
}

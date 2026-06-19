using Microsoft.Data.Sqlite;
using Scalar.AspNetCore;
using TradingEngine.Host;
using TradingEngine.Infrastructure.Indicators;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Infrastructure.Persistence.Reporting;
using TradingEngine.Risk.Compliance;
using TradingEngine.Services;
using TradingEngine.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddControllers();

builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy =
        System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddSingleton<RunProgressBroadcaster>();

// OpenAPI + Scalar
builder.Services.AddOpenApi();

var dbPath = builder.Configuration.GetValue<string>("Persistence:DbPath")
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "data", "trading.db"));

Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContext<TradingDbContext>(opt =>
    opt.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddDbContext<ReportingDbContext>(opt =>
    opt.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddScoped<IBacktestRunRepository, SqliteBacktestRunRepository>();
builder.Services.AddScoped<IBarRepository, SqliteBarRepository>();
builder.Services.AddScoped(_ => new TradeReportQueries(new SqliteConnection($"Data Source={dbPath}")));
builder.Services.AddScoped<IPipelineEventRepository, SqlitePipelineEventRepository>();
builder.Services.AddScoped<IExperimentRepository, SqliteExperimentRepository>();
builder.Services.AddScoped<ITradeRepository, SqliteTradeRepository>();
builder.Services.AddScoped<IEquityRepository, SqliteEquityRepository>();
builder.Services.AddScoped<IStrategyConfigStore, SqliteStrategyConfigStore>();
builder.Services.AddSingleton<IExperimentHostFactory, ExperimentHostFactoryAdapter>();
builder.Services.AddTransient<ExperimentRunner>();
builder.Services.AddSingleton<BacktestProgressStore>();
builder.Services.AddSingleton<BacktestJournal>();
builder.Services.AddSingleton<RunProjection>();
builder.Services.AddSingleton<EffectiveConfigResolver>();
builder.Services.AddSingleton<BacktestOrchestrator>();
builder.Services.AddSingleton<IBacktestCommandService>(sp => sp.GetRequiredService<BacktestOrchestrator>());
builder.Services.AddSingleton<IBacktestQueryService, BacktestQueryService>();

// Query services — no DbContext in controllers
builder.Services.AddScoped<IRunQueryService, RunQueryService>();
builder.Services.AddScoped<IProtectionQueryService, ProtectionQueryService>();
builder.Services.AddScoped<IBarQueryService, BarQueryService>();

// Register strategy bank infrastructure for APIs + Razor Pages
builder.Services.AddSingleton<IIndicatorService, SkenderIndicatorService>();
builder.Services.AddSingleton<IRegimeDetector, AtrBasedRegimeDetector>();
builder.Services.AddSingleton<IPassProbabilityEstimator, PassProbabilityEstimator>();
builder.Services.AddSingleton<ISymbolInfoRegistry>(_ =>
{
    var solRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    var catalog = new SymbolCatalog(solRoot);
    var reg = new SymbolInfoRegistry();
    foreach (var si in catalog.GetAll()) reg.Register(si);
    return reg;
});
builder.Services.AddSingleton<StrategyRegistry>();
builder.Services.AddSingleton<IStrategyBank>(sp => new StrategyBankService(
    sp.GetRequiredService<StrategyRegistry>(),
    null,
    null,
    sp.GetRequiredService<ILogger<StrategyBankService>>()));
builder.Services.AddSingleton<ITradingGovernor, TradingGovernorService>();
builder.Services.AddSingleton(new GovernorOptions());
builder.Services.AddSingleton(new RegimeOptions());
builder.Services.AddSingleton<ProtectionLedgerWriter>();

using var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
    await db.Database.MigrateAsync();

    var solRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<StrategyConfigSeeder>>();
    var seeder = new StrategyConfigSeeder(
        scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
        solRoot, logger);
    await seeder.SeedAsync();
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapControllers();
app.MapHub<TradingEngine.Web.Hubs.RunHub>("/hubs/run");

app.MapOpenApi();
app.MapScalarApiReference();

app.Run();

public partial class Program { }

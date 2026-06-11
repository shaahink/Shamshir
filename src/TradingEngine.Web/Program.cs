using Microsoft.Data.Sqlite;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Infrastructure.Persistence.Reporting;
using TradingEngine.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddControllers();

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
builder.Services.AddSingleton<BacktestProgressStore>();
builder.Services.AddSingleton<BacktestOrchestrator>();
builder.Services.AddSingleton<IBacktestCommandService>(sp => sp.GetRequiredService<BacktestOrchestrator>());
builder.Services.AddSingleton<IBacktestQueryService, BacktestQueryService>();

using (var ctx = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite($"Data Source={dbPath}").Options))
{
    ctx.Database.EnsureCreated();
    try { ctx.Database.ExecuteSqlRaw("ALTER TABLE TradeResults ADD COLUMN RunId TEXT NULL;"); } catch { }
    try { ctx.Database.ExecuteSqlRaw("ALTER TABLE BacktestRuns ADD COLUMN AlgoHash TEXT NOT NULL DEFAULT '';"); } catch { }
    try { ctx.Database.ExecuteSqlRaw("ALTER TABLE BacktestRuns ADD COLUMN StrategyParamsJson TEXT NOT NULL DEFAULT '{}';"); } catch { }
    try { ctx.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS BarEvaluations (Id TEXT PRIMARY KEY, RunId TEXT NOT NULL, Symbol TEXT NOT NULL, Timeframe TEXT NOT NULL, BarOpenTimeUtc TEXT NOT NULL, StrategyId TEXT NOT NULL, IndicatorValuesJson TEXT NOT NULL DEFAULT '{}', SignalFired INTEGER NOT NULL DEFAULT 0, SignalDirection TEXT, Reason TEXT NOT NULL DEFAULT '', OccurredAtUtc TEXT NOT NULL);"); } catch { }
    try { ctx.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_BarEvaluations_RunId ON BarEvaluations(RunId);"); } catch { }
    try { ctx.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_BarEvaluations_RunId_StrategyId_BarOpenTimeUtc ON BarEvaluations(RunId, StrategyId, BarOpenTimeUtc);"); } catch { }

    if (!builder.Configuration.GetValue<bool>("CTrader:UseForBacktest") && !ctx.Bars.Any())
    {
        Console.WriteLine(
            "WARNING: Bars table is empty. Run scripts/seed-bars.ps1 before starting with CTrader:UseForBacktest=false.");
    }
}

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapControllers();

app.Run();

public partial class Program { }

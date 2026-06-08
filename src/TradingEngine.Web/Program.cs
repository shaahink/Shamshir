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
builder.Services.AddScoped(_ => new TradeReportQueries(new SqliteConnection($"Data Source={dbPath}")));
builder.Services.AddSingleton<BacktestOrchestrator>();

using (var ctx = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite($"Data Source={dbPath}").Options))
{
    ctx.Database.EnsureCreated();
    try { ctx.Database.ExecuteSqlRaw("ALTER TABLE TradeResults ADD COLUMN RunId TEXT NULL;"); } catch { }
}

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapControllers();

app.Run();

public partial class Program { }

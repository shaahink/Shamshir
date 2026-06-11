using Microsoft.Data.Sqlite;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Infrastructure.Persistence.Reporting;
using TradingEngine.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddControllers();
builder.Services.AddServerSideBlazor();

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
builder.Services.AddSingleton<BacktestProgressStore>();
builder.Services.AddSingleton<BacktestJournal>();
builder.Services.AddSingleton<BacktestOrchestrator>();
builder.Services.AddSingleton<IBacktestCommandService>(sp => sp.GetRequiredService<BacktestOrchestrator>());
builder.Services.AddSingleton<IBacktestQueryService, BacktestQueryService>();

using var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
    await db.Database.MigrateAsync();
}

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapControllers();
app.MapBlazorHub();
app.MapFallbackToPage("/blazor/_Host");

app.Run();

public partial class Program { }

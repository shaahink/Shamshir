using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddControllers();

var dbPath = builder.Configuration.GetValue<string>("Persistence:DbPath")
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "trading.db"));

builder.Services.AddDbContext<TradingDbContext>(opt =>
    opt.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddScoped<IBacktestRunRepository, SqliteBacktestRunRepository>();
builder.Services.AddSingleton<BacktestOrchestrator>();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();
app.MapControllers();

app.Run();

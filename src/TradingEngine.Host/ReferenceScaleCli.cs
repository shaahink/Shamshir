using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Host;

/// <summary>
/// Host CLI verb <c>compute-reference-scales</c> (iter-parity-pipeline P1.1, AUDIT F10) — populates the
/// per (symbol, timeframe) <c>ReferenceScales</c> cells from the shared market-data DB into the SAME
/// trading DB the Web app uses. Builds a minimal service provider (the full engine host does not register
/// the market-data store) and fails loud if the trading DB has pending migrations.
/// </summary>
public static class ReferenceScaleCli
{
    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var root = DbPathResolver.FindRepoRoot();
        var dbPath = DbPathResolver.ResolveTradingDbPath(config.GetValue<string>("Persistence:DbPath"));
        var mdPath = DbPathResolver.ResolveMarketDataDbPath(config.GetValue<string>("MarketData:DbPath"), dbPath);

        Log.Information("compute-reference-scales: trading DB {DbPath}", dbPath);
        Log.Information("compute-reference-scales: market-data DB {MarketDataPath}", mdPath);

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(Log.Logger));
        services.AddDbContext<TradingDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.AddDbContextFactory<MarketDataDbContext>(o => o.UseSqlite($"Data Source={mdPath}"));
        services.AddSingleton<IMarketDataStore, SqliteMarketDataStore>();
        services.AddSingleton<ISymbolInfoRegistry>(_ =>
        {
            var reg = new SymbolInfoRegistry();
            foreach (var si in new SymbolCatalog(root).GetAll()) reg.Register(si);
            return reg;
        });
        services.AddSingleton<ReferenceScalePopulator>();

        await using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("MigrationGuard");
            MigrationGuard.EnsureUpToDate(db, dbPath, logger);
        }

        var populator = provider.GetRequiredService<ReferenceScalePopulator>();
        var updated = await populator.PopulateAllAsync(ct);
        Log.Information("compute-reference-scales: {Cells} reference-scale cell(s) updated", updated);
        return updated > 0 ? 0 : 1;
    }
}

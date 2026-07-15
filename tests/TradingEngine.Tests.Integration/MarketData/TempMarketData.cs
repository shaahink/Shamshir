using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Tests.Integration.MarketData;

/// <summary>Temp-file-backed market-data store + ingester for tests (in-memory SQLite can't be shared
/// across factory-created connections). Disposable — deletes the temp DB.</summary>
internal sealed class TempMarketData : IDisposable
{
    private readonly string _path;
    public SqliteMarketDataStore Store { get; }
    public MarketDataIngester Ingester { get; }

    public TempMarketData()
    {
        _path = Path.Combine(Path.GetTempPath(), $"mdtest-{Guid.NewGuid():N}.db");
        var opts = new DbContextOptionsBuilder<MarketDataDbContext>().UseSqlite($"Data Source={_path}").Options;
        using (var db = new MarketDataDbContext(opts)) db.Database.EnsureCreated();
        Store = new SqliteMarketDataStore(new Factory(opts));
        Ingester = new MarketDataIngester(Store);
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            File.Delete(_path);
            File.Delete(_path + "-wal");
            File.Delete(_path + "-shm");
        }
        catch { /* best effort */ }
    }

    private sealed class Factory(DbContextOptions<MarketDataDbContext> opts) : IDbContextFactory<MarketDataDbContext>
    {
        public MarketDataDbContext CreateDbContext() => new(opts);
    }
}

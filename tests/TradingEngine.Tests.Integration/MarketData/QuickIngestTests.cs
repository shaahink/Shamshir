using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Tests.Integration.MarketData;

/// <summary>Ingests shards from <c>data/shards/</c> into <c>data/marketdata.db</c>.</summary>
public sealed class QuickIngestTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _shardsDir;
    private SqliteMarketDataStore? _store;
    private MarketDataIngester? _ingester;

    public QuickIngestTests()
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _dbPath = Path.Combine(solutionRoot, "src", "TradingEngine.Web", "data", "marketdata.db");
        _shardsDir = Path.Combine(solutionRoot, "src", "TradingEngine.Web", "data", "shards");
    }

    [Fact]
    public async Task Ingest_All_Shards_Into_MarketDataDb()
    {
        if (!Directory.Exists(_shardsDir))
        {
            Directory.CreateDirectory(_shardsDir);
            return;
        }

        var shards = Directory.GetFiles(_shardsDir, "*.ndjson");
        if (shards.Length == 0) return;

        if (File.Exists(_dbPath)) File.Delete(_dbPath);

        var opts = new DbContextOptionsBuilder<MarketDataDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options;
        using (var db = new MarketDataDbContext(opts)) db.Database.EnsureCreated();

        var factory = new Factory(opts);
        _store = new SqliteMarketDataStore(factory);
        _ingester = new MarketDataIngester(_store);

        int total = 0;
        foreach (var shard in shards)
        {
            var result = await _ingester.IngestFileAsync(shard, "ctrader");
            total += result.BarsInserted;
        }

        Assert.True(total > 0, $"No bars ingested from {shards.Length} shards");

        var inventory = await _store.GetInventoryAsync();
        Assert.NotEmpty(inventory);
    }

    private sealed class Factory(DbContextOptions<MarketDataDbContext> opts) : IDbContextFactory<MarketDataDbContext>
    {
        public MarketDataDbContext CreateDbContext() => new(opts);
    }

    public void Dispose()
    {
        // keep DB
    }
}

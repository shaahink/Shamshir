using Microsoft.EntityFrameworkCore;
using TradingEngine.CTraderRunner;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Tests.Simulation.E2E;

[Collection("CtraderSerial")]
[Trait("RequiresCTrader", "true")]
public sealed class MarketDataBulkDownloadE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _shardsDir;
    private readonly string _dbPath;

    public MarketDataBulkDownloadE2ETests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "shamshir-bulk", Guid.NewGuid().ToString("N")[..8]);
        _shardsDir = Path.Combine(_tempDir, "shards");
        _dbPath = Path.Combine(_tempDir, "marketdata.db");
        Directory.CreateDirectory(_shardsDir);
    }

    private static bool HasCredentials =>
        !string.IsNullOrEmpty(CtraderTestHelpers.ResolveCredential("CtId", "CTrader__CtId"));

    [SkippableFact]
    public async Task Download_EurUsd_H1_M1_ThreeDays_And_GbpUsd_H1_ThreeDays_All_Ingest_Cleanly()
    {
        Skip.IfNot(HasCredentials, "No cTrader credentials");

        var end = new DateTime(2025, 6, 5, 0, 0, 0, DateTimeKind.Utc);
        var start = end.AddDays(-3);

        var downloads = new (string Symbol, string[] Periods)[]
        {
            ("EURUSD", new[] { "h1", "m1" }),
            ("GBPUSD", new[] { "h1" }),
        };

        int totalBarsRecorded = 0;

        foreach (var (symbol, periods) in downloads)
        {
            var periodsStr = string.Join(",", periods);
            var req = new BacktestCliRequest
            {
                AlgoPath = CtraderTestHelpers.ResolveAlgo(),
                Symbol = symbol,
                Period = periods[0],
                Start = start,
                End = end,
                CtId = CtraderTestHelpers.ResolveCredential("CtId", "CTrader__CtId"),
                PwdFile = CtraderTestHelpers.ResolveCredential("PwdFile", "CTrader__PwdFile"),
                Account = CtraderTestHelpers.ResolveCredential("Account", "CTrader__Account"),
                DataPort = 15560,
                CommandPort = 15561,
                Balance = 100_000m,
                FullAccess = true,
                DataMode = "m1",
                ReportDir = _shardsDir,
                Record = true,
                Periods = [periodsStr],
            };

            await BacktestCli.InvokeAsync(req, CancellationToken.None);

            foreach (var tf in periods)
            {
                var shardPath = Path.Combine(_shardsDir, $"{symbol}_{tf}.ndjson");
                Assert.True(File.Exists(shardPath), $"Shard missing: {shardPath}");
                var lines = File.ReadAllLines(shardPath);
                Assert.NotEmpty(lines);
                totalBarsRecorded += lines.Length;
            }
        }

        var opts = new DbContextOptionsBuilder<MarketDataDbContext>()
            .UseSqlite($"Data Source={_dbPath}").Options;
        using (var db = new MarketDataDbContext(opts)) db.Database.EnsureCreated();
        var factory = new TestDbContextFactory(opts);
        var store = new SqliteMarketDataStore(factory);
        var ingester = new MarketDataIngester(store);

        foreach (var file in Directory.GetFiles(_shardsDir, "*.ndjson"))
        {
            var result = await ingester.IngestFileAsync(file, "ctrader");
            Assert.True(result.BarsInserted > 0, $"No bars ingested from {Path.GetFileName(file)}");
        }

        var inventory = await store.GetInventoryAsync();
        Assert.True(inventory.Count >= 3, $"Expected >=3 entries, got {inventory.Count}");

        foreach (var entry in inventory)
        {
            Assert.True(entry.BarCount > 0, $"Zero bars for {entry.Symbol}/{entry.Timeframe}");
        }

        var targetPath = Path.Combine(
            CtraderTestHelpers.SolutionRoot, "src", "TradingEngine.Web", "data", "marketdata.db");
        var targetDir = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(targetDir);
        if (File.Exists(targetPath)) File.Delete(targetPath);
        File.Copy(_dbPath, targetPath);

        // Also copy the shards next to the DB for reference
        var shardsDest = Path.Combine(targetDir, "shards");
        if (Directory.Exists(shardsDest)) Directory.Delete(shardsDest, true);
        CopyDirectory(_shardsDir, shardsDest);
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
    }

    private sealed class TestDbContextFactory(DbContextOptions<MarketDataDbContext> opts)
        : IDbContextFactory<MarketDataDbContext>
    {
        public MarketDataDbContext CreateDbContext() => new(opts);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }
}

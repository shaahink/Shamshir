using Microsoft.EntityFrameworkCore;
using TradingEngine.CTraderRunner;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Tests.Simulation.E2E;

[Collection("CtraderSerial")]
[Trait("Category", "CtraderContract")]
[Trait("RequiresCTrader", "true")]
public sealed class MarketDataRecorderE2ETests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _reportDir;

    public MarketDataRecorderE2ETests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "shamshir-e2e-record", Guid.NewGuid().ToString("N")[..8]);
        _reportDir = Path.Combine(_tempDir, "shards");
        Directory.CreateDirectory(_reportDir);
    }

    private static bool HasCredentials =>
        !string.IsNullOrEmpty(CtraderTestHelpers.ResolveCredential("CtId", "CTrader__CtId"));

    [SkippableFact]
    public async Task Record_EurUsd_M1_1Day_ProducesShard_And_IngestsCleanly()
    {
        Skip.IfNot(HasCredentials, "No cTrader credentials");

        var end = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var start = end.AddDays(-1);

        var req = new BacktestCliRequest
        {
            AlgoPath = CtraderTestHelpers.ResolveAlgo(),
            Symbol = "EURUSD",
            Period = "m1",
            Start = start,
            End = end,
            CtId = CtraderTestHelpers.ResolveCredential("CtId", "CTrader__CtId"),
            PwdFile = CtraderTestHelpers.ResolveCredential("PwdFile", "CTrader__PwdFile"),
            Account = CtraderTestHelpers.ResolveCredential("Account", "CTrader__Account"),
            DataPort = 15557,
            CommandPort = 15558,
            Balance = 100_000m,
            FullAccess = true,
            DataMode = "m1",
            ReportDir = _reportDir,
            Record = true,
            Periods = ["m1"],
        };

        var result = await BacktestCli.InvokeAsync(req, CancellationToken.None);

        var shardPath = Path.Combine(_reportDir, "EURUSD_m1.ndjson");
        Assert.True(File.Exists(shardPath),
            $"Shard not found at {shardPath}. CLI exit={result.ExitCode}. CBOT lines: {string.Join("; ", result.CbotLines)}");

        var lines = await File.ReadAllLinesAsync(shardPath);
        Assert.NotEmpty(lines);
        Assert.True(lines.Length >= 100, $"Expected >=100 bars, got {lines.Length} for 1 day of m1");

        int parsedOk = 0;
        foreach (var line in lines.Take(5))
        {
            if (MarketDataShardIo.TryParse(line, out var bar))
            {
                Assert.Equal(Symbol.Parse("EURUSD"), bar.Symbol);
                Assert.Equal(Timeframe.M1, bar.Timeframe);
                Assert.NotEqual(default, bar.OpenTimeUtc);
                Assert.True(bar.High >= bar.Low, $"High({bar.High}) < Low({bar.Low})");
                parsedOk++;
            }
        }
        Assert.Equal(5, parsedOk);

        var dbPath = Path.Combine(_tempDir, "marketdata.db");
        var opts = new DbContextOptionsBuilder<MarketDataDbContext>().UseSqlite($"Data Source={dbPath}").Options;
        using (var db = new MarketDataDbContext(opts)) db.Database.EnsureCreated();
        var factory = new TestDbContextFactory(opts);
        var store = new SqliteMarketDataStore(factory);
        var ingester = new MarketDataIngester(store);

        var ingestResult = await ingester.IngestFileAsync(shardPath, "ctrader");
        Assert.True(ingestResult.BarsInserted >= 100, $"Expected >=100 bars ingested, got {ingestResult.BarsInserted}");

        var inventory = await store.GetInventoryAsync();
        Assert.NotEmpty(inventory);
        var eurM1 = inventory.First(i => i.Symbol == "EURUSD" && i.Timeframe == Timeframe.M1);
        Assert.True(eurM1.BarCount >= 100);

        var bars = await store.ReadBarsAsync(Symbol.Parse("EURUSD"), Timeframe.M1, start, end);
        Assert.NotEmpty(bars);

        var reIngest = await ingester.IngestFileAsync(shardPath, "ctrader");
        Assert.Equal(0, reIngest.BarsInserted);
    }

    private sealed class TestDbContextFactory(DbContextOptions<MarketDataDbContext> opts) : IDbContextFactory<MarketDataDbContext>
    {
        public MarketDataDbContext CreateDbContext() => new(opts);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }
}

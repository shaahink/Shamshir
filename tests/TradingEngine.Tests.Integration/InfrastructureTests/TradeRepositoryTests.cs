using Microsoft.EntityFrameworkCore;
using TradingEngine.Tests.Integration.Support;

namespace TradingEngine.Tests.Integration.InfrastructureTests;

public sealed class TradeRepositoryTests : IDisposable
{
    private readonly SqliteInMemory _mem = new();

    public void Dispose() => _mem.Dispose();

    private TradingDbContext CreateInMemoryDb() => _mem.NewContext();

    [Fact]
    public async Task SaveAndRetrieve_RoundTrips()
    {
        using var db = CreateInMemoryDb();
        var repo = new SqliteTradeRepository(db);

        var trade = new TradeResult(
            Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"),
            TradeDirection.Long, 0.1m,
            new Price(1.08420m), new Price(1.08700m),
            new Price(1.08210m), new Price(1.08900m),
            new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 1, 1, 14, 0, 0, DateTimeKind.Utc),
            new Money(28.0m, "USD"), new Money(0.50m, "USD"),
            new Money(0m, "USD"), new Money(27.50m, "USD"),
            new Pips(28), 2.8, new Pips(5), new Pips(30),
            "TP", "test-strategy", "standard", EngineMode.Backtest);

        await repo.SaveAsync(trade, "test-run", CancellationToken.None);

        var retrieved = await repo.GetByDateRangeAsync(
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            CancellationToken.None);

        retrieved.Should().HaveCount(1);
        retrieved[0].Id.Should().Be(trade.Id);
        retrieved[0].Symbol.Should().Be(trade.Symbol);
        retrieved[0].Direction.Should().Be(trade.Direction);
        retrieved[0].Lots.Should().Be(trade.Lots);
        retrieved[0].NetPnL.Amount.Should().Be(27.50m);
        retrieved[0].ExitReason.Should().Be("TP");
    }

    [Fact]
    public async Task BulkInsert_StoresAllBars()
    {
        using var db = CreateInMemoryDb();
        var repo = new SqliteBarRepository(db);

        var bars = Enumerable.Range(0, 100).Select(i => new Bar(
            Symbol.Parse("EURUSD"), Timeframe.H1,
            new DateTime(2024, 1, 1).AddHours(i),
            1.0800m + i * 0.0001m, 1.0805m + i * 0.0001m,
            1.0795m + i * 0.0001m, 1.0802m + i * 0.0001m, 1000 + i)).ToList();

        await repo.BulkInsertAsync(bars, CancellationToken.None);

        var retrieved = await repo.GetAsync(
            Symbol.Parse("EURUSD"), Timeframe.H1,
            new DateTime(2024, 1, 1), new DateTime(2024, 12, 31),
            CancellationToken.None);

        retrieved.Should().HaveCount(100);
    }

    [Fact]
    public async Task EventLog_AppendOnly_HasNoUpdateMethod()
    {
        using var db = CreateInMemoryDb();
        var repo = new SqliteEventLogRepository(db);

        var evt = new TradeOpened(
            new Position(Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"),
                TradeDirection.Long, 0.1m, new Price(1.08420m),
                new Price(1.08210m), null,
                DateTime.UtcNow, "test"),
            DateTime.UtcNow);

        await repo.AppendAsync(evt, CancellationToken.None);

        var recent = await repo.GetRecentAsync(10, CancellationToken.None);
        recent.Should().HaveCount(1);
        recent[0].Should().BeOfType<TradeOpened>();
    }

    // P3.1: the excursion recorder's persistence side -- a separate table from TradeResult (a few hundred
    // bytes of compact JSON per trade), keyed by (RunId, PositionId).
    [Fact]
    public async Task Excursion_SaveAndRetrieve_RoundTrips()
    {
        using var db = CreateInMemoryDb();
        var repo = new SqliteExcursionRepository(db);
        var positionId = Guid.NewGuid();
        const string pathJson = """[{"t":0,"hi":8.0,"lo":-4.0},{"t":60,"hi":-27.0,"lo":-52.0}]""";

        await repo.SaveAsync("run-1", positionId, pathJson, CancellationToken.None);

        var retrieved = await repo.GetAsync("run-1", positionId, CancellationToken.None);
        retrieved.Should().Be(pathJson);

        // A different run/position must not collide.
        (await repo.GetAsync("run-2", positionId, CancellationToken.None)).Should().BeNull();
    }
}

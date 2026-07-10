using System.Text.Json;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Tests.Integration.Support;

namespace TradingEngine.Tests.Integration.InfrastructureTests;

/// <summary>
/// P2.5 gate: Thesis/ExpectedTradesPerWeek/ExpectedHoldBars must round-trip through the real SQLite store
/// (Upsert -> GetAll), for both a brand-new row and an update to an existing one.
/// </summary>
public sealed class StrategyConfigStoreTests : IDisposable
{
    private readonly SqliteInMemory _mem = new();

    public void Dispose() => _mem.Dispose();

    private static StrategyConfigEntry MakeEntry(string id, string? thesis, int? tradesPerWeek, int? holdBars) =>
        new(id, id, true, "standard", JsonDocument.Parse("{}").RootElement)
        {
            Thesis = thesis,
            ExpectedTradesPerWeek = tradesPerWeek,
            ExpectedHoldBars = holdBars,
        };

    [Fact]
    public async Task UpsertAndGetAll_NewEntry_RoundTripsThesisMetadata()
    {
        using var db = _mem.NewContext();
        var store = new SqliteStrategyConfigStore(db);

        await store.UpsertAsync(MakeEntry("trend-breakout", "Fresh breakouts continue.", 3, 20), CancellationToken.None);

        using var db2 = _mem.NewContext();
        var store2 = new SqliteStrategyConfigStore(db2);
        var all = await store2.GetAllAsync(CancellationToken.None);

        var entry = all.Should().ContainSingle().Which;
        entry.Thesis.Should().Be("Fresh breakouts continue.");
        entry.ExpectedTradesPerWeek.Should().Be(3);
        entry.ExpectedHoldBars.Should().Be(20);
    }

    [Fact]
    public async Task UpsertAsync_UpdatingExistingEntry_UpdatesThesisMetadata()
    {
        using var db = _mem.NewContext();
        var store = new SqliteStrategyConfigStore(db);
        await store.UpsertAsync(MakeEntry("ema-alignment", "Old thesis.", 1, 10), CancellationToken.None);

        using var db2 = _mem.NewContext();
        var store2 = new SqliteStrategyConfigStore(db2);
        await store2.UpsertAsync(MakeEntry("ema-alignment", "Updated thesis.", 2, 15), CancellationToken.None);

        using var db3 = _mem.NewContext();
        var store3 = new SqliteStrategyConfigStore(db3);
        var entry = (await store3.GetAllAsync(CancellationToken.None)).Should().ContainSingle().Which;

        entry.Thesis.Should().Be("Updated thesis.");
        entry.ExpectedTradesPerWeek.Should().Be(2);
        entry.ExpectedHoldBars.Should().Be(15);
    }

    [Fact]
    public async Task UpsertAndGetAll_NullThesisMetadata_RoundTripsAsNull()
    {
        using var db = _mem.NewContext();
        var store = new SqliteStrategyConfigStore(db);

        await store.UpsertAsync(MakeEntry("mean-reversion", null, null, null), CancellationToken.None);

        using var db2 = _mem.NewContext();
        var store2 = new SqliteStrategyConfigStore(db2);
        var entry = (await store2.GetAllAsync(CancellationToken.None)).Should().ContainSingle().Which;

        entry.Thesis.Should().BeNull();
        entry.ExpectedTradesPerWeek.Should().BeNull();
        entry.ExpectedHoldBars.Should().BeNull();
    }
}

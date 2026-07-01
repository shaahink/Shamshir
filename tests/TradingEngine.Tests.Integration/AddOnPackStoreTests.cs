using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Web.Configuration;

namespace TradingEngine.Tests.Integration;

/// <summary>
/// iter-38 PK1: the SQLite add-on-pack store round-trips a pack (including its nested add-ons + regime toggle)
/// over real SQLite (:memory:, per D10), and the 3 seeded starter packs are well-formed.
/// </summary>
public sealed class AddOnPackStoreTests
{
    private static TradingDbContext NewDb(SqliteConnection conn)
    {
        var opts = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(conn).Options;
        var db = new TradingDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task Roundtrips_a_pack_with_nested_addons_and_regime_toggle()
    {
        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        using var db = NewDb(conn);
        var store = new SqliteAddOnPackStore(db);

        var pack = new AddOnPack("runner-aggressive", "Runner", "desc",
            new PositionManagementOptions
            {
                Trailing = new TrailingOptions { Enabled = true, Mode = AddOnMode.Auto, Method = "AtrMultiple" },
                PartialTp = new PartialTpOptions { Enabled = true, Mode = AddOnMode.Auto },
            },
            RegimeDetectionEnabled: false);

        await store.UpsertAsync(pack, default);
        var got = await store.GetByIdAsync("runner-aggressive", default);

        got.Should().NotBeNull();
        got!.Name.Should().Be("Runner");
        got.AddOns.Trailing.Enabled.Should().BeTrue();
        got.AddOns.Trailing.Method.Should().Be("AtrMultiple");
        got.AddOns.PartialTp!.Enabled.Should().BeTrue();
        got.RegimeDetectionEnabled.Should().BeFalse();
    }

    [Fact]
    public void Starter_packs_are_three_distinct_ids()
        => AddOnPackSeeder.StarterPacks.Select(p => p.Id).Distinct().Should().HaveCount(3);
}

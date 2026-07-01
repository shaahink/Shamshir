using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace TradingEngine.Tests.Integration;

/// <summary>
/// iter-38 D5 / Stream T2: the EF SaveChanges interceptor auto-stamps audit timestamps with an injected clock.
/// CreatedAtUtc is set once on insert and is immutable thereafter; UpdatedAtUtc moves on every change. Runs on
/// real SQLite (:memory:) per D10.
/// </summary>
public sealed class AuditStampInterceptorTests
{
    private static TradingDbContext NewDb(SqliteConnection conn, Func<DateTime> clock)
    {
        var opts = new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlite(conn)
            .AddInterceptors(new AuditStampInterceptor(clock))
            .Options;
        var db = new TradingDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task Insert_stamps_both_then_update_moves_only_UpdatedAtUtc()
    {
        var t0 = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 1, 2, 9, 30, 0, DateTimeKind.Utc);
        var now = t0;

        using var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        using var db = NewDb(conn, () => now);

        var row = new GovernorOptionsEntity { Id = "audit-test", Json = "{}" };
        db.Add(row);
        await db.SaveChangesAsync();

        row.CreatedAtUtc.Should().Be(t0);
        row.UpdatedAtUtc.Should().Be(t0);

        now = t1;
        row.Json = "{\"changed\":true}";
        await db.SaveChangesAsync();

        row.CreatedAtUtc.Should().Be(t0, "CreatedAtUtc must be immutable after insert");
        row.UpdatedAtUtc.Should().Be(t1, "UpdatedAtUtc must move on every change");
    }
}

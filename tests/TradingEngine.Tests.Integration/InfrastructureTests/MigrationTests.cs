using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace TradingEngine.Tests.Integration.InfrastructureTests;

public sealed class MigrationTests
{
    [Fact]
    public async Task FreshDatabase_MigrateAsync_DoesNotThrow()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"migrate_test_{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            using var db = new TradingDbContext(options);
            var act = () => db.Database.MigrateAsync();
            await act.Should().NotThrowAsync("fresh DB migration must succeed");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    // P1.1 (F10): the Host CLI must fail loud against a stale schema.
    [Fact]
    public void MigrationGuard_MigratedDatabase_DoesNotThrow()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"guard_ok_{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlite($"Data Source={dbPath}").Options;

            using (var seed = new TradingDbContext(options))
                seed.Database.Migrate();

            using var db = new TradingDbContext(options);
            MigrationGuard.GetPending(db).Should().BeEmpty();
            var act = () => MigrationGuard.EnsureUpToDate(db, dbPath, NullLogger.Instance);
            act.Should().NotThrow();
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public void MigrationGuard_UnmigratedDatabase_FailsLoudWithPath()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"guard_stale_{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlite($"Data Source={dbPath}").Options;

            using var db = new TradingDbContext(options);
            MigrationGuard.GetPending(db).Should().NotBeEmpty("an un-migrated DB has every migration pending");

            var act = () => MigrationGuard.EnsureUpToDate(db, dbPath, NullLogger.Instance);
            act.Should().Throw<PendingMigrationsException>()
                .Which.DbPath.Should().Be(dbPath);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    private static void Cleanup(string dbPath)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (int i = 0; i < 10 && File.Exists(dbPath); i++)
        {
            try { File.Delete(dbPath); break; }
            catch (IOException) { Thread.Sleep(200); }
        }
    }
}

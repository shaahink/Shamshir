using Microsoft.EntityFrameworkCore;

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
            GC.Collect();
            GC.WaitForPendingFinalizers();
            for (int i = 0; i < 10 && File.Exists(dbPath); i++)
            {
                try { File.Delete(dbPath); break; }
                catch (IOException) { Thread.Sleep(200); }
            }
        }
    }
}

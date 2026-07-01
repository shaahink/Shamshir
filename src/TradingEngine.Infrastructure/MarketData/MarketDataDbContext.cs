using Microsoft.EntityFrameworkCore;

namespace TradingEngine.Infrastructure.MarketData;

/// <summary>
/// Dedicated EF context for the canonical market-data store, backed by its OWN SQLite file
/// (<c>marketdata.db</c>) — kept separate from <c>trading.db</c> so long-lived shared history and churny
/// per-run data don't share a write lock or a lifecycle (PLAN §5 / D1). Created via
/// <see cref="Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions"/> EnsureCreated at startup;
/// it has no migration history of its own by design (single, stable table).
/// </summary>
public sealed class MarketDataDbContext(DbContextOptions<MarketDataDbContext> options) : DbContext(options)
{
    public DbSet<MarketDataBarRow> Bars => Set<MarketDataBarRow>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<MarketDataBarRow>(e =>
        {
            e.ToTable("MarketDataBars");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            // Dedupe + fast range scan: unique on the natural key, plus a coverage index for inventory.
            e.HasIndex(x => new { x.Symbol, x.Timeframe, x.OpenTimeUtc }).IsUnique();
            e.HasIndex(x => new { x.Symbol, x.Timeframe });
        });
    }
}

namespace TradingEngine.Infrastructure.Persistence;

public sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options) : DbContext(options)
{
    public DbSet<TradeResultEntity> Trades => Set<TradeResultEntity>();
    public DbSet<EquitySnapshotEntity> EquitySnapshots => Set<EquitySnapshotEntity>();
    public DbSet<EngineEventEntity> Events => Set<EngineEventEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TradeResultMapping());
        modelBuilder.ApplyConfiguration(new EquitySnapshotMapping());
        modelBuilder.ApplyConfiguration(new EngineEventMapping());
    }
}

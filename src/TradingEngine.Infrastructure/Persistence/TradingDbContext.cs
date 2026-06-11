namespace TradingEngine.Infrastructure.Persistence;

public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<TradeResultEntity> Trades => Set<TradeResultEntity>();
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<PositionEntity> Positions => Set<PositionEntity>();
    public DbSet<EngineEventEntity> Events => Set<EngineEventEntity>();
    public DbSet<EquitySnapshotEntity> EquitySnapshots => Set<EquitySnapshotEntity>();
    public DbSet<BarEntity> Bars => Set<BarEntity>();
    public DbSet<BarEvaluationEntity> BarEvaluations => Set<BarEvaluationEntity>();
    public DbSet<BacktestRunEntity> BacktestRuns => Set<BacktestRunEntity>();
    public DbSet<PipelineEventEntity> PipelineEvents => Set<PipelineEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TradeResultMapping());
        modelBuilder.ApplyConfiguration(new OrderMapping());
        modelBuilder.ApplyConfiguration(new PositionMapping());
        modelBuilder.ApplyConfiguration(new EngineEventMapping());
        modelBuilder.ApplyConfiguration(new EquitySnapshotMapping());
        modelBuilder.ApplyConfiguration(new BarMapping());
        modelBuilder.ApplyConfiguration(new PipelineEventMapping());

        modelBuilder.Entity<BacktestRunEntity>(e =>
        {
            e.ToTable("BacktestRuns");
            e.HasKey(x => x.RunId);
        });

        modelBuilder.Entity<BarEvaluationEntity>(e =>
        {
            e.ToTable("BarEvaluations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => new { x.RunId, x.StrategyId, x.BarOpenTimeUtc });
        });
    }
}

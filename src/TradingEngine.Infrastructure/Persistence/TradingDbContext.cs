using TradingEngine.Infrastructure.Persistence.Entities;

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
    public DbSet<ExperimentEntity> Experiments => Set<ExperimentEntity>();
    public DbSet<ExperimentRunEntity> ExperimentRuns => Set<ExperimentRunEntity>();
    public DbSet<DailyProtectionLedgerEntity> DailyProtectionLedgers => Set<DailyProtectionLedgerEntity>();
    public DbSet<ProtectionLedgerEntryEntity> ProtectionLedgerEntries => Set<ProtectionLedgerEntryEntity>();
    public DbSet<StrategyConfigEntity> StrategyConfigs => Set<StrategyConfigEntity>();
    public DbSet<RiskProfileEntity> RiskProfiles => Set<RiskProfileEntity>();
    public DbSet<PropFirmRuleSetEntity> PropFirmRuleSets => Set<PropFirmRuleSetEntity>();
    public DbSet<GovernorOptionsEntity> GovernorOptions => Set<GovernorOptionsEntity>();
    public DbSet<DatasetEntity> Datasets => Set<DatasetEntity>();
    public DbSet<ConfigSetEntity> ConfigSets => Set<ConfigSetEntity>();

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
            e.Property(x => x.EffectiveConfigJson).HasColumnType("TEXT");
        });

        modelBuilder.Entity<BarEvaluationEntity>(e =>
        {
            e.ToTable("BarEvaluations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => new { x.RunId, x.StrategyId, x.BarOpenTimeUtc });
        });

        modelBuilder.Entity<ExperimentEntity>(e =>
        {
            e.ToTable("Experiments");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<ExperimentRunEntity>(e =>
        {
            e.ToTable("ExperimentRuns");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ExperimentId);
            e.HasIndex(x => x.BacktestRunId);
            e.HasOne(x => x.Experiment)
                .WithMany(x => x.Runs)
                .HasForeignKey(x => x.ExperimentId);
        });

        modelBuilder.Entity<DailyProtectionLedgerEntity>(e =>
        {
            e.ToTable("DailyProtectionLedgers");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => x.Date);
        });

        modelBuilder.Entity<ProtectionLedgerEntryEntity>(e =>
        {
            e.ToTable("ProtectionLedgerEntries");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.LedgerId);
            e.HasIndex(x => x.AtUtc);
            e.HasOne(x => x.Ledger)
                .WithMany(x => x.Entries)
                .HasForeignKey(x => x.LedgerId);
        });

        modelBuilder.Entity<StrategyConfigEntity>(e =>
        {
            e.ToTable("StrategyConfigs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.DisplayName).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.DefaultSymbols).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Timeframe).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.RiskProfileId).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.ParametersJson).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.PositionManagementJson).HasColumnType("TEXT");
            e.Property(x => x.OrderEntryJson).HasColumnType("TEXT");
            e.Property(x => x.RegimeFilterJson).HasColumnType("TEXT");
            e.Property(x => x.ReentryJson).HasColumnType("TEXT");
            e.Property(x => x.UpdatedAtUtc).HasColumnType("TEXT");
        });

        modelBuilder.Entity<RiskProfileEntity>(e =>
        {
            e.ToTable("RiskProfiles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.DisplayName).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Json).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.UpdatedAtUtc).HasColumnType("TEXT");
        });

        modelBuilder.Entity<PropFirmRuleSetEntity>(e =>
        {
            e.ToTable("PropFirmRuleSets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.DisplayName).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Json).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.UpdatedAtUtc).HasColumnType("TEXT");
        });

        modelBuilder.Entity<GovernorOptionsEntity>(e =>
        {
            e.ToTable("GovernorOptions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Json).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.UpdatedAtUtc).HasColumnType("TEXT");
        });

        modelBuilder.Entity<DatasetEntity>(e =>
        {
            e.ToTable("Datasets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.ContentHash).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Symbols).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Timeframes).HasColumnType("TEXT").IsRequired();
            e.HasIndex(x => x.ContentHash);
        });

        modelBuilder.Entity<ConfigSetEntity>(e =>
        {
            e.ToTable("ConfigSets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.ContentHash).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Json).HasColumnType("TEXT").IsRequired();
            e.HasIndex(x => x.ContentHash);
        });
    }
}

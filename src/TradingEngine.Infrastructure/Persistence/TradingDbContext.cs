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
    public DbSet<BacktestRunEntity> BacktestRuns => Set<BacktestRunEntity>();
    public DbSet<ExperimentEntity> Experiments => Set<ExperimentEntity>();
    public DbSet<ExperimentRunEntity> ExperimentRuns => Set<ExperimentRunEntity>();
    public DbSet<StrategyConfigEntity> StrategyConfigs => Set<StrategyConfigEntity>();
    public DbSet<RiskProfileEntity> RiskProfiles => Set<RiskProfileEntity>();
    public DbSet<PropFirmRuleSetEntity> PropFirmRuleSets => Set<PropFirmRuleSetEntity>();
    public DbSet<GovernorOptionsEntity> GovernorOptions => Set<GovernorOptionsEntity>();
    public DbSet<DatasetEntity> Datasets => Set<DatasetEntity>();
    public DbSet<ConfigSetEntity> ConfigSets => Set<ConfigSetEntity>();
    public DbSet<JournalEntryEntity> JournalEntries => Set<JournalEntryEntity>();
    public DbSet<AddOnPackEntity> AddOnPacks => Set<AddOnPackEntity>();   // iter-38 PK1
    public DbSet<VenueSessionEntity> VenueSessions => Set<VenueSessionEntity>();
    public DbSet<TradeExcursionEntity> TradeExcursions => Set<TradeExcursionEntity>();
    public DbSet<ExitCalibrationEntity> ExitCalibrations => Set<ExitCalibrationEntity>();
    public DbSet<ReferenceScaleEntity> ReferenceScales => Set<ReferenceScaleEntity>();
    public DbSet<WalkForwardJobEntity> WalkForwardJobs => Set<WalkForwardJobEntity>();
    public DbSet<WalkForwardWindowResultEntity> WalkForwardWindowResults => Set<WalkForwardWindowResultEntity>();
    public DbSet<StrategyCellParkEntity> StrategyCellParks => Set<StrategyCellParkEntity>();
    public DbSet<ResearchPipelineEntity> ResearchPipelines => Set<ResearchPipelineEntity>();
    public DbSet<ResearchPipelineStepEntity> ResearchPipelineSteps => Set<ResearchPipelineStepEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new TradeResultMapping());
        modelBuilder.ApplyConfiguration(new OrderMapping());
        modelBuilder.ApplyConfiguration(new PositionMapping());
        modelBuilder.ApplyConfiguration(new EngineEventMapping());
        modelBuilder.ApplyConfiguration(new EquitySnapshotMapping());
        modelBuilder.ApplyConfiguration(new BarMapping());
        modelBuilder.ApplyConfiguration(new TradeExcursionMapping());
        modelBuilder.ApplyConfiguration(new ExitCalibrationMapping());
        modelBuilder.ApplyConfiguration(new ReferenceScaleMapping());
        modelBuilder.ApplyConfiguration(new WalkForwardJobMapping());
        modelBuilder.ApplyConfiguration(new WalkForwardWindowResultMapping());
        modelBuilder.ApplyConfiguration(new ResearchPipelineMapping());
        modelBuilder.ApplyConfiguration(new ResearchPipelineStepMapping());

        modelBuilder.Entity<StrategyCellParkEntity>(e =>
        {
            e.ToTable("StrategyCellParks");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.StrategyId, x.Symbol, x.Timeframe }).IsUnique();
        });

        modelBuilder.Entity<BacktestRunEntity>(e =>
        {
            e.ToTable("BacktestRuns");
            e.HasKey(x => x.RunId);
            e.Property(x => x.EffectiveConfigJson).HasColumnType("TEXT");
            e.Property(x => x.WarningsJson).HasColumnType("TEXT");
            e.HasIndex(x => x.StartedAtUtc);
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

        modelBuilder.Entity<StrategyConfigEntity>(e =>
        {
            e.ToTable("StrategyConfigs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.DisplayName).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.RiskProfileId).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.ParametersJson).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.PositionManagementJson).HasColumnType("TEXT");
            e.Property(x => x.OrderEntryJson).HasColumnType("TEXT");
            e.Property(x => x.RegimeFilterJson).HasColumnType("TEXT");
            e.Property(x => x.ReentryJson).HasColumnType("TEXT");
            e.Property(x => x.EntryFilterJson).HasColumnType("TEXT");
            e.Property(x => x.Version).HasColumnType("INTEGER").IsRequired();
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

        modelBuilder.Entity<JournalEntryEntity>(e =>
        {
            e.ToTable("Journal");
            e.HasKey(x => new { x.RunId, x.Seq });
            e.Property(x => x.RunId).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Seq).HasColumnType("INTEGER");
            e.Property(x => x.SimTimeUtc).HasColumnType("TEXT");
            e.Property(x => x.EventKind).HasColumnType("TEXT").IsRequired();
            e.HasIndex(x => new { x.RunId, x.SimTimeUtc });
        });

        // iter-38 PK1 — reusable add-on packs (owner decision D1).
        modelBuilder.Entity<AddOnPackEntity>(e =>
        {
            e.ToTable("AddOnPacks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Name).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Description).HasColumnType("TEXT");
            e.Property(x => x.AddOnsJson).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.CreatedAtUtc).HasColumnType("TEXT");
            e.Property(x => x.UpdatedAtUtc).HasColumnType("TEXT");
        });

        modelBuilder.Entity<VenueSessionEntity>(e =>
        {
            e.ToTable("VenueSessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.RunId).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Venue).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Event).HasColumnType("TEXT").IsRequired();
            e.Property(x => x.Detail).HasColumnType("TEXT");
            e.HasIndex(x => x.RunId);
            e.HasIndex(x => x.OccurredAtUtc);
        });
    }
}

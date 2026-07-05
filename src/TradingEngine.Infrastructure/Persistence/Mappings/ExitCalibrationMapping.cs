using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Mappings;

public sealed class ExitCalibrationMapping : IEntityTypeConfiguration<ExitCalibrationEntity>
{
    public void Configure(EntityTypeBuilder<ExitCalibrationEntity> builder)
    {
        builder.ToTable("ExitCalibrations");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.StrategyId).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.Symbol).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.EntryTimeframe).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.Regime).HasColumnType("TEXT");
        builder.HasIndex(e => new { e.StrategyId, e.Symbol, e.EntryTimeframe, e.Regime }).IsUnique();
    }
}

public sealed class ReferenceScaleMapping : IEntityTypeConfiguration<ReferenceScaleEntity>
{
    public void Configure(EntityTypeBuilder<ReferenceScaleEntity> builder)
    {
        builder.ToTable("ReferenceScales");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Symbol).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.EntryTimeframe).HasColumnType("TEXT").IsRequired();
        builder.HasIndex(e => new { e.Symbol, e.EntryTimeframe }).IsUnique();
    }
}

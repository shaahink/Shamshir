using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Mappings;

public sealed class WalkForwardJobMapping : IEntityTypeConfiguration<WalkForwardJobEntity>
{
    public void Configure(EntityTypeBuilder<WalkForwardJobEntity> builder)
    {
        builder.ToTable("WalkForwardJobs");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.SpecJson).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(32);
        builder.HasMany(e => e.Windows).WithOne().HasForeignKey(w => w.JobId);
    }
}

public sealed class WalkForwardWindowResultMapping : IEntityTypeConfiguration<WalkForwardWindowResultEntity>
{
    public void Configure(EntityTypeBuilder<WalkForwardWindowResultEntity> builder)
    {
        builder.ToTable("WalkForwardWindowResults");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.JobId, e.WindowIndex }).IsUnique();
        builder.Property(e => e.StrategyId).HasMaxLength(128);
        builder.Property(e => e.Symbol).HasMaxLength(32);
        builder.Property(e => e.Timeframe).HasMaxLength(8);
        builder.Property(e => e.ChosenParamsJson).IsRequired();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Mappings;

public sealed class PipelineEventMapping : IEntityTypeConfiguration<PipelineEventEntity>
{
    public void Configure(EntityTypeBuilder<PipelineEventEntity> builder)
    {
        builder.ToTable("PipelineEvents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.RunId).IsRequired();
        builder.Property(x => x.Stage).IsRequired().HasMaxLength(32);
        builder.Property(x => x.CorrelationId).HasMaxLength(64);
        builder.Property(x => x.DetailJson).HasMaxLength(4096);
        builder.HasIndex(x => new { x.RunId, x.Seq });
    }
}

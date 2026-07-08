using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Infrastructure.Persistence.Mappings;

/// <summary>P3.2 (Q6) — schema for the research-pipeline state tables. Steps cascade off the pipeline;
/// (PipelineId, StepIndex) is unique so a resume never double-inserts a step.</summary>
public sealed class ResearchPipelineMapping : IEntityTypeConfiguration<ResearchPipelineEntity>
{
    public void Configure(EntityTypeBuilder<ResearchPipelineEntity> builder)
    {
        builder.ToTable("ResearchPipelines");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Name).HasMaxLength(128).IsRequired();
        builder.Property(e => e.PlaybookJson).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(32).IsRequired();
        builder.HasIndex(e => e.StartedAtUtc);
        builder.HasMany(e => e.Steps).WithOne().HasForeignKey(s => s.PipelineId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ResearchPipelineStepMapping : IEntityTypeConfiguration<ResearchPipelineStepEntity>
{
    public void Configure(EntityTypeBuilder<ResearchPipelineStepEntity> builder)
    {
        builder.ToTable("ResearchPipelineSteps");
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => new { e.PipelineId, e.StepIndex }).IsUnique();
        builder.Property(e => e.Kind).HasMaxLength(32).IsRequired();
        builder.Property(e => e.Status).HasMaxLength(32).IsRequired();
        builder.Property(e => e.ParamHash).HasMaxLength(64);
    }
}

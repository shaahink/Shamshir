namespace TradingEngine.Infrastructure.Persistence.Mappings;

public sealed class EngineEventMapping : IEntityTypeConfiguration<EngineEventEntity>
{
    public void Configure(EntityTypeBuilder<EngineEventEntity> builder)
    {
        builder.ToTable("EngineEvents");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.EventType).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.Payload).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.OccurredAtUtc).HasColumnType("TEXT");
        builder.HasIndex(e => e.EventType);
        builder.HasIndex(e => e.OccurredAtUtc);
    }
}

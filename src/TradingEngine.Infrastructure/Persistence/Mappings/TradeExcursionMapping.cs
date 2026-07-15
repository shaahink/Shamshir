namespace TradingEngine.Infrastructure.Persistence.Mappings;

public sealed class TradeExcursionMapping : IEntityTypeConfiguration<TradeExcursionEntity>
{
    public void Configure(EntityTypeBuilder<TradeExcursionEntity> builder)
    {
        builder.ToTable("TradeExcursions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.RunId).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.PathJson).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.SessionLabel).HasColumnType("TEXT");
        builder.HasIndex(e => e.RunId);
        builder.HasIndex(e => new { e.RunId, e.PositionId }).IsUnique();
    }
}

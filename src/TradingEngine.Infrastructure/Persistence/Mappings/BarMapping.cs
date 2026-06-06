namespace TradingEngine.Infrastructure.Persistence.Mappings;

public sealed class BarMapping : IEntityTypeConfiguration<BarEntity>
{
    public void Configure(EntityTypeBuilder<BarEntity> builder)
    {
        builder.ToTable("Bars");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Symbol).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.Timeframe).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.OpenTimeUtc).HasColumnType("TEXT");
        builder.Property(e => e.Open).HasColumnType("TEXT");
        builder.Property(e => e.High).HasColumnType("TEXT");
        builder.Property(e => e.Low).HasColumnType("TEXT");
        builder.Property(e => e.Close).HasColumnType("TEXT");
        builder.HasIndex(e => new { e.Symbol, e.Timeframe, e.OpenTimeUtc });
    }
}

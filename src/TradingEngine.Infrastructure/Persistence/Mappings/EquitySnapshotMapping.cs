namespace TradingEngine.Infrastructure.Persistence.Mappings;

public sealed class EquitySnapshotMapping : IEntityTypeConfiguration<EquitySnapshotEntity>
{
    public void Configure(EntityTypeBuilder<EquitySnapshotEntity> builder)
    {
        builder.ToTable("EquitySnapshots");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.TimestampUtc).HasColumnType("TEXT");
        builder.Property(e => e.Balance).HasColumnType("REAL");
        builder.Property(e => e.FloatingPnL).HasColumnType("REAL");
        builder.Property(e => e.Equity).HasColumnType("REAL");
        builder.Property(e => e.PeakEquity).HasColumnType("REAL");
        builder.Property(e => e.DailyStartEquity).HasColumnType("REAL");
        builder.Property(e => e.CurrentDailyDrawdown).HasColumnType("REAL");
        builder.Property(e => e.CurrentMaxDrawdown).HasColumnType("REAL");
        builder.Property(e => e.Mode).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.RunId).HasColumnType("TEXT");
        builder.HasIndex(e => e.TimestampUtc);
        builder.HasIndex(e => e.RunId);
    }
}

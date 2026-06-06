namespace TradingEngine.Infrastructure.Persistence.Mappings;

public sealed class PositionMapping : IEntityTypeConfiguration<PositionEntity>
{
    public void Configure(EntityTypeBuilder<PositionEntity> builder)
    {
        builder.ToTable("Positions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Symbol).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.Direction).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.EntryPrice).HasColumnType("TEXT");
        builder.Property(e => e.CurrentStopLoss).HasColumnType("TEXT");
        builder.Property(e => e.TakeProfit).HasColumnType("TEXT");
        builder.Property(e => e.OpenedAtUtc).HasColumnType("TEXT");
        builder.Property(e => e.ClosedAtUtc).HasColumnType("TEXT");
        builder.Property(e => e.StrategyId).HasColumnType("TEXT").IsRequired();
    }
}

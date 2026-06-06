namespace TradingEngine.Infrastructure.Persistence.Mappings;

public sealed class OrderMapping : IEntityTypeConfiguration<OrderEntity>
{
    public void Configure(EntityTypeBuilder<OrderEntity> builder)
    {
        builder.ToTable("Orders");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Symbol).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.Direction).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.OrderType).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.State).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.FillPrice).HasColumnType("TEXT");
        builder.Property(e => e.StopLoss).HasColumnType("TEXT");
        builder.Property(e => e.TakeProfit).HasColumnType("TEXT");
        builder.Property(e => e.CreatedAtUtc).HasColumnType("TEXT");
        builder.Property(e => e.FilledAtUtc).HasColumnType("TEXT");
        builder.Property(e => e.StrategyId).HasColumnType("TEXT").IsRequired();
        builder.HasIndex(e => e.State);
    }
}

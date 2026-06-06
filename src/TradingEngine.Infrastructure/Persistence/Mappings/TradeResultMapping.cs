namespace TradingEngine.Infrastructure.Persistence.Mappings;

public sealed class TradeResultMapping : IEntityTypeConfiguration<TradeResultEntity>
{
    public void Configure(EntityTypeBuilder<TradeResultEntity> builder)
    {
        builder.ToTable("TradeResults");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Symbol).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.Direction).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.EntryPrice).HasColumnType("TEXT");
        builder.Property(e => e.ExitPrice).HasColumnType("TEXT");
        builder.Property(e => e.StopLoss).HasColumnType("TEXT");
        builder.Property(e => e.TakeProfit).HasColumnType("TEXT");
        builder.Property(e => e.OpenedAtUtc).HasColumnType("TEXT");
        builder.Property(e => e.ClosedAtUtc).HasColumnType("TEXT");
        builder.Property(e => e.GrossPnLAmount).HasColumnType("TEXT");
        builder.Property(e => e.CommissionAmount).HasColumnType("TEXT");
        builder.Property(e => e.SwapAmount).HasColumnType("TEXT");
        builder.Property(e => e.NetPnLAmount).HasColumnType("TEXT");
        builder.Property(e => e.ExitReason).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.StrategyId).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.RiskProfileId).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.Mode).HasColumnType("TEXT").IsRequired();
        builder.HasIndex(e => e.ClosedAtUtc);
        builder.HasIndex(e => e.StrategyId);
    }
}

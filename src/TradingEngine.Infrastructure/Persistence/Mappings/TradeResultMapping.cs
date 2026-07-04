namespace TradingEngine.Infrastructure.Persistence.Mappings;

public sealed class TradeResultMapping : IEntityTypeConfiguration<TradeResultEntity>
{
    public void Configure(EntityTypeBuilder<TradeResultEntity> builder)
    {
        builder.ToTable("TradeResults");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Symbol).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.Direction).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.Lots).HasColumnType("REAL");
        builder.Property(e => e.EntryPrice).HasColumnType("REAL");
        builder.Property(e => e.ExitPrice).HasColumnType("REAL");
        builder.Property(e => e.StopLoss).HasColumnType("REAL");
        builder.Property(e => e.TakeProfit).HasColumnType("REAL");
        builder.Property(e => e.InitialStopLoss).HasColumnType("REAL");
        builder.Property(e => e.OpenedAtUtc).HasColumnType("TEXT");
        builder.Property(e => e.ClosedAtUtc).HasColumnType("TEXT");
        builder.Property(e => e.GrossPnLAmount).HasColumnType("REAL");
        builder.Property(e => e.CommissionAmount).HasColumnType("REAL");
        builder.Property(e => e.SwapAmount).HasColumnType("REAL");
        builder.Property(e => e.NetPnLAmount).HasColumnType("REAL");
        builder.Property(e => e.ExitReason).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.StrategyId).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.RiskProfileId).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.Mode).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.OrderEntryMethod).HasColumnType("TEXT");
        builder.HasIndex(e => e.RunId);
        builder.HasIndex(e => e.ClosedAtUtc);
        builder.HasIndex(e => e.StrategyId);
    }
}

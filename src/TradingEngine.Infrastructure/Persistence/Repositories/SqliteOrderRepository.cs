namespace TradingEngine.Infrastructure.Persistence.Repositories;

public sealed class SqliteOrderRepository(TradingDbContext db) : IOrderRepository
{
    public async Task SaveAsync(Order order, CancellationToken ct)
    {
        var entity = new OrderEntity
        {
            Id = order.Id,
            Symbol = order.Intent.Symbol.ToString(),
            Direction = order.Intent.Direction.ToString(),
            OrderType = order.Intent.OrderType.ToString(),
            State = order.State.ToString(),
            RequestedLots = order.RequestedLots,
            FillPrice = order.FillPrice?.Value,
            FilledLots = order.FilledLots,
            RejectionReason = order.RejectionReason,
            CreatedAtUtc = order.CreatedAtUtc,
            FilledAtUtc = order.FilledAtUtc,
            StopLoss = order.Intent.StopLoss.Value,
            TakeProfit = order.Intent.TakeProfit?.Value,
            StrategyId = order.Intent.StrategyId,
            RiskProfileId = order.Intent.RiskProfileId,
            Reason = order.Intent.Reason,
        };
        db.Orders.Add(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Order?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        var entity = await db.Orders.FindAsync([id], ct);
        return entity is null ? null : MapToDomain(entity);
    }

    public async Task<IReadOnlyList<Order>> GetByPositionAsync(Guid positionId, CancellationToken ct)
    {
        return await db.Orders
            .Where(o => o.Id == positionId)
            .Select(o => MapToDomain(o))
            .ToListAsync(ct);
    }

    private static Order MapToDomain(OrderEntity e)
    {
        var intent = new TradeIntent(
            Symbol.Parse(e.Symbol),
            Enum.Parse<TradeDirection>(e.Direction),
            Enum.Parse<OrderType>(e.OrderType),
            e.LimitPrice is not null ? new Price(decimal.Parse(e.LimitPrice)) : null,
            new Price(e.StopLoss ?? 0),
            e.TakeProfit.HasValue ? new Price(e.TakeProfit.Value) : null,
            e.StrategyId, e.RiskProfileId, e.Reason, e.CreatedAtUtc);

        return new Order(
            e.Id, intent, e.RequestedLots,
            Enum.Parse<OrderState>(e.State),
            e.FillPrice.HasValue ? new Price(e.FillPrice.Value) : null,
            e.FilledLots, e.CreatedAtUtc, e.FilledAtUtc, e.RejectionReason);
    }
}

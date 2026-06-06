namespace TradingEngine.Domain;

public interface IOrderRepository
{
    Task SaveAsync(Order order, CancellationToken ct);
    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Order>> GetByPositionAsync(Guid positionId, CancellationToken ct);
}

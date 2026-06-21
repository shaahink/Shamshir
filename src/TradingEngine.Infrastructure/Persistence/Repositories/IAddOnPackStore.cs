using TradingEngine.Domain;

namespace TradingEngine.Infrastructure.Persistence.Repositories;

/// <summary>iter-38 (Stream PK1). CRUD over reusable <see cref="AddOnPack"/>s. Mirrors <c>IStrategyConfigStore</c>.</summary>
public interface IAddOnPackStore
{
    Task<IReadOnlyList<AddOnPack>> GetAllAsync(CancellationToken ct);
    Task<AddOnPack?> GetByIdAsync(string id, CancellationToken ct);
    Task UpsertAsync(AddOnPack pack, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}

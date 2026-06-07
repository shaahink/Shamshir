namespace TradingEngine.Domain;

public interface IEquityRepository
{
    Task SaveAsync(EquitySnapshot snapshot, CancellationToken ct);
    Task SaveBatchAsync(IReadOnlyList<EquitySnapshot> snapshots, CancellationToken ct);
    Task<IReadOnlyList<EquitySnapshot>> GetByDateRangeAsync(DateTime from, DateTime to, CancellationToken ct);
    Task<EquitySnapshot?> GetLatestAsync(CancellationToken ct);
}

namespace TradingEngine.Domain;

public interface IEquityRepository
{
    Task SaveAsync(EquitySnapshot snapshot, string? runId, CancellationToken ct);
    Task SaveBatchAsync(IReadOnlyList<EquitySnapshot> snapshots, string? runId, CancellationToken ct);
    Task<IReadOnlyList<EquitySnapshot>> GetByDateRangeAsync(DateTime from, DateTime to, CancellationToken ct);
    Task<IReadOnlyList<EquitySnapshot>> GetByRunIdAsync(string runId, CancellationToken ct);
    Task<EquitySnapshot?> GetLatestAsync(CancellationToken ct);
}

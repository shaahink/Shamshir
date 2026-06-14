namespace TradingEngine.Domain;

public interface IAccountSnapshotStore
{
    Task<IReadOnlyList<AccountSnapshot>> GetByRunIdAsync(string runId, CancellationToken ct);
}

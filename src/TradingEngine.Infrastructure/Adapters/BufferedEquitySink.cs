using System.Collections.Concurrent;

namespace TradingEngine.Infrastructure.Adapters;

public sealed class BufferedEquitySink : IEquitySink, IAccountSnapshotStore
{
    private readonly ConcurrentQueue<AccountSnapshot> _snapshots = new();

    public void Observe(AccountSnapshot snapshot)
    {
        _snapshots.Enqueue(snapshot);
    }

    public Task<IReadOnlyList<AccountSnapshot>> GetByRunIdAsync(string runId, CancellationToken ct)
    {
        var list = _snapshots.ToArray();
        return Task.FromResult<IReadOnlyList<AccountSnapshot>>(list);
    }

    public IReadOnlyList<AccountSnapshot> GetSnapshots() => _snapshots.ToArray();
}

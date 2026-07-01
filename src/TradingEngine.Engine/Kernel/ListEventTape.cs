using TradingEngine.Domain;

namespace TradingEngine.Engine;

/// <summary>
/// A pure in-memory tape over a materialized event list (iter-35 A1/A4). Used by tests, the golden
/// replay oracle, and as the shape a DB-backed <c>BarTape</c> implements (read Bars → BarClosed stream).
/// Deterministic: replays the exact events in order, no clock, no randomness.
/// </summary>
public sealed class ListEventTape(DatasetRef dataset, IReadOnlyList<EngineEvent> events) : IEventTape
{
    public DatasetRef Dataset { get; } = dataset;

    public async IAsyncEnumerable<EngineEvent> ReadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var e in events)
        {
            ct.ThrowIfCancellationRequested();
            yield return e;
        }
        await Task.CompletedTask;
    }
}

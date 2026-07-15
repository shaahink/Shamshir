using System.Collections.Concurrent;

namespace TradingEngine.Web.Services;

/// <summary>In-memory registry of live (non-finalized) runs, shared by the orchestrator (owner:
/// registers on Start, removes on finalize) and the venue runners (readers: late progress events
/// must drop silently once a run has been removed).</summary>
public sealed class RunRegistry
{
    private readonly ConcurrentDictionary<string, BacktestRunState> _runs = new();

    public BacktestRunState? Get(string runId) =>
        _runs.TryGetValue(runId, out var state) ? state : null;

    public bool TryGet(string runId, out BacktestRunState state) =>
        _runs.TryGetValue(runId, out state!);

    /// <summary>Indexer semantics of the pre-refactor dictionary: throws when the run is unknown.
    /// Use only where the run is guaranteed registered (the run's own execution path).</summary>
    public BacktestRunState GetRequired(string runId) => _runs[runId];

    public IReadOnlyList<BacktestRunState> All() => _runs.Values.ToList();

    public void Register(BacktestRunState state) => _runs[state.RunId] = state;

    public bool Remove(string runId) => _runs.TryRemove(runId, out _);
}

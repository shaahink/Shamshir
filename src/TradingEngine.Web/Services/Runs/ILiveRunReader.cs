namespace TradingEngine.Web.Services;

/// <summary>Read-side port over the live (non-finalized) runs the orchestrator owns: current
/// in-memory state, the live-run list, and queue position. Query services observe live runs
/// through this seam instead of depending on the command-side <see cref="BacktestOrchestrator"/>
/// concrete class (the read side needs none of its command surface).</summary>
public interface ILiveRunReader
{
    BacktestRunState? GetState(string runId);

    IReadOnlyList<BacktestRunState> GetAll();

    /// <summary>1-based position in the admission queue, or null when the run is not queued.</summary>
    int? GetQueuePosition(string runId);
}

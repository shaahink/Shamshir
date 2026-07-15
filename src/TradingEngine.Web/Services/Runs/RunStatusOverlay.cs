namespace TradingEngine.Web.Services;

/// <summary>Shared status-truth rules for the run read paths: how long a "running"/"queued" row
/// with no live in-memory state may age before it is reported stuck, and the single-run status
/// resolution that prefers the persisted terminal Status column. Used by both the list overlay
/// (RunListQuery) and the detail path (RunDetailQuery).</summary>
public static class RunStatusOverlay
{
    public static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(30);

    public static string ResolveStatus(BacktestRunSummary r, ILiveRunReader? liveRuns)
    {
        // X0: an explicit terminal Status column (written by WriteEndRecordAsync) beats the legacy
        // ExitCode/ErrorMessage-only inference below, which has no way to represent "cancelled" — any
        // non-null ErrorMessage falls out as Failed there, so a cancelled run misreported as failed once
        // it aged out of the live in-memory overlay (found in the X0 cancel-mid-queue smoke test). Rows
        // written before this column existed carry Status="" and fall through unaffected.
        if (r.Status is not null && RunStateMachine.TerminalStates.Contains(r.Status))
        {
            return r.Status;
        }

        var isStuck = r.CompletedAtUtc == default
            && DateTime.UtcNow - r.StartedAtUtc > StuckThreshold
            && liveRuns?.GetState(r.RunId) is null;

        return RunStatusResolver.Resolve(
            isCompleted: r.CompletedAtUtc != default,
            errorMessage: r.ErrorMessage,
            warningsJson: r.WarningsJson,
            isStuck: isStuck);
    }
}

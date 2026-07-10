namespace TradingEngine.Domain;

/// <summary>
/// P0.2 (F5, Q5) — the single source of truth for a run's status string. Before this, four readers
/// (RunQueryService list + detail, BacktestQueryService, BacktestAnalyticsController) each derived
/// status inline as <c>ErrorMessage != null ? "failed" : "completed"</c>, which conflated an
/// engine-result failure with a transport/persistence teardown fault — so a fully-complete cTrader run
/// whose NetMQ teardown threw was stamped <c>failed</c> (the audited F5 bug).
///
/// Vocabulary (Q5): <c>failed</c> is reserved for "no trustworthy result" (ErrorMessage set, no
/// warnings). A run that produced a complete result but hit a teardown/persistence anomaly is
/// <c>completed-with-warnings</c> (WarningsJson populated). <c>completed</c> is a clean run.
/// </summary>
public static class RunStatusResolver
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string CompletedWithWarnings = "completed-with-warnings";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>
    /// Resolve the terminal/live status from the persisted summary fields.
    /// <paramref name="isStuck"/> lets the caller fold in its own "no live orchestrator state and past
    /// the stuck threshold" heuristic for a still-open row.
    /// </summary>
    public static string Resolve(
        bool isCompleted, string? errorMessage, string? warningsJson, bool isStuck = false)
    {
        if (!isCompleted)
            return isStuck ? Failed : Running;

        // Completed. A trustworthy result exists unless ErrorMessage says otherwise.
        if (!string.IsNullOrWhiteSpace(errorMessage))
            return Failed;

        return HasWarnings(warningsJson) ? CompletedWithWarnings : Completed;
    }

    public static bool HasWarnings(string? warningsJson) =>
        !string.IsNullOrWhiteSpace(warningsJson)
        && warningsJson.Trim() is not ("{}" or "[]" or "null");
}

namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.2 — the seam the <see cref="PlaybookExecutor"/> uses to run ONE step against the running app. Real
/// impl calls the ResearchCli verbs' underlying HTTP endpoints; a fake drives the executor's logic in
/// unit tests credential-free. A step runner never persists pipeline state (that is
/// <see cref="IPipelineStore"/>) and never decides control flow (that is the executor) — it just does the
/// step and reports a machine verdict.
/// </summary>
public interface IStepRunner
{
    Task<StepOutcome> RunAsync(PlaybookStep step, PipelineContext context, CancellationToken ct);
}

/// <summary>
/// The result of running one step. <see cref="Kind"/> separates a passing step, a failing step, and an
/// <c>owner-gate</c> that must PARK the pipeline (awaiting-owner) — the executor branches on this, not on
/// string parsing. <see cref="VerdictJson"/> is stored verbatim (the resume + review source of truth);
/// <see cref="ArtifactPath"/> points at any file the step wrote under the pipeline's artifact dir.
/// </summary>
public sealed record StepOutcome(StepOutcomeKind Kind, string VerdictJson, string? ArtifactPath = null)
{
    public static StepOutcome Pass(string verdictJson, string? artifact = null) => new(StepOutcomeKind.Passed, verdictJson, artifact);

    public static StepOutcome Fail(string verdictJson, string? artifact = null) => new(StepOutcomeKind.Failed, verdictJson, artifact);

    public static StepOutcome AwaitOwner(string verdictJson) => new(StepOutcomeKind.AwaitingOwner, verdictJson);
}

public enum StepOutcomeKind
{
    Passed,
    Failed,
    AwaitingOwner,
}

/// <summary>
/// Mutable per-pipeline scratchpad passed to each step (e.g. a <c>start-run</c> step records the runId a
/// later <c>await-run</c>/<c>assert-gates</c> step reads). Kept tiny and string-keyed so the executor
/// stays a dumb sequential walker — no typed dependency graph (PLAN §12).
/// </summary>
public sealed class PipelineContext
{
    public Guid PipelineId { get; init; }

    public string? ArtifactDir { get; init; }

    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
}

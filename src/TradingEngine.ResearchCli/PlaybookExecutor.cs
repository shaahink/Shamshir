namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.2 (the centerpiece) — the dumb sequential playbook executor. It walks a playbook's steps in order,
/// runs each via <see cref="IStepRunner"/>, and persists every verdict via <see cref="IPipelineStore"/>.
/// It has NO DAG, NO parallelism, NO retry policy (PLAN §12) — resumability + honest persisted verdicts
/// are the whole value. Control flow is exactly three rules:
///
/// 1. A step that FAILS stops the pipeline (status <c>failed</c>) UNLESS its <c>continueOnFail</c> is set.
/// 2. An <c>owner-gate</c> that returns <see cref="StepOutcomeKind.AwaitingOwner"/> PARKS the pipeline
///    (status <c>awaiting-owner</c>) and returns — the owner approves in the UI / via
///    <c>research pipeline approve</c>, then a <c>--resume</c> continues past the (now approved) gate.
/// 3. On <c>--resume</c>, a step whose recorded verdict is <c>passed|approved|skipped</c> AND whose
///    param hash is unchanged is SKIPPED; the first step with a changed hash (or a non-passed status)
///    re-runs, and everything after it re-runs too (content-addressed invalidation).
/// </summary>
public sealed class PlaybookExecutor
{
    private readonly IStepRunner _runner;
    private readonly IPipelineStore _store;

    public PlaybookExecutor(IStepRunner runner, IPipelineStore store)
    {
        _runner = runner;
        _store = store;
    }

    /// <summary>Start a fresh pipeline from a playbook.</summary>
    public async Task<PipelineResult> RunAsync(Playbook playbook, string playbookJson, string? artifactDir, CancellationToken ct)
    {
        var record = await _store.CreateAsync(playbook, playbookJson, artifactDir, ct);
        // Auto-create a default artifact dir so steps always have a place to write files.
        artifactDir ??= DefaultArtifactDir(record.Id);
        return await ExecuteAsync(playbook, record, artifactDir, resume: false, ct);
    }

    /// <summary>Resume an existing pipeline by id, skipping still-valid passed steps.</summary>
    public async Task<PipelineResult> ResumeAsync(Playbook playbook, Guid pipelineId, string? artifactDir, CancellationToken ct)
    {
        var record = await _store.GetAsync(pipelineId, ct);
        artifactDir ??= DefaultArtifactDir(pipelineId);
        return await ExecuteAsync(playbook, record, artifactDir, resume: true, ct);
    }

    private async Task<PipelineResult> ExecuteAsync(Playbook playbook, PipelineRecord record, string? artifactDir, bool resume, CancellationToken ct)
    {
        var context = new PipelineContext { PipelineId = record.Id, ArtifactDir = artifactDir };
        var startIndex = resume ? FirstInvalidStepIndex(playbook, record) : 0;

        await _store.SetPipelineStatusAsync(record.Id, "running", startIndex, completed: false, ct);

        for (var i = startIndex; i < playbook.Steps.Count; i++)
        {
            var step = playbook.Steps[i];
            await _store.SetPipelineStatusAsync(record.Id, "running", i, completed: false, ct);
            await _store.SetStepStatusAsync(record.Id, i, "running", null, null, step.ParamHash, ct);

            StepOutcome outcome;
            try
            {
                outcome = await _runner.RunAsync(step, context, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var verdict = Verdict.Failing(
                    VerdictField.Of("step", step.Kind),
                    VerdictField.Of("error", ex.GetType().Name)).Render();
                await _store.SetStepStatusAsync(record.Id, i, "failed", verdict, null, step.ParamHash, ct);
                await _store.SetPipelineStatusAsync(record.Id, "failed", i, completed: true, ct);
                return PipelineResult.Failed(record.Id, i, verdict);
            }

            switch (outcome.Kind)
            {
                case StepOutcomeKind.AwaitingOwner:
                    await _store.SetStepStatusAsync(record.Id, i, "awaiting-owner", outcome.VerdictJson, outcome.ArtifactPath, step.ParamHash, ct);
                    await _store.SetPipelineStatusAsync(record.Id, "awaiting-owner", i, completed: false, ct);
                    return PipelineResult.AwaitingOwner(record.Id, i);

                case StepOutcomeKind.Failed:
                    await _store.SetStepStatusAsync(record.Id, i, "failed", outcome.VerdictJson, outcome.ArtifactPath, step.ParamHash, ct);
                    if (!step.ContinueOnFail)
                    {
                        await _store.SetPipelineStatusAsync(record.Id, "failed", i, completed: true, ct);
                        return PipelineResult.Failed(record.Id, i, outcome.VerdictJson);
                    }
                    break;

                case StepOutcomeKind.Passed:
                default:
                    await _store.SetStepStatusAsync(record.Id, i, "passed", outcome.VerdictJson, outcome.ArtifactPath, step.ParamHash, ct);
                    break;
            }
        }

        await _store.SetPipelineStatusAsync(record.Id, "completed", playbook.Steps.Count, completed: true, ct);
        return PipelineResult.Completed(record.Id);
    }

    /// <summary>
    /// The first step index that must re-run on resume: the earliest step whose recorded status is not a
    /// clean pass (passed/approved/skipped) OR whose param hash no longer matches the playbook. Everything
    /// from there onward re-runs (content-addressed downstream invalidation). If every step is a valid
    /// pass, returns <c>Steps.Count</c> (nothing to do).
    /// </summary>
    public static int FirstInvalidStepIndex(Playbook playbook, PipelineRecord record)
    {
        for (var i = 0; i < playbook.Steps.Count; i++)
        {
            var step = playbook.Steps[i];
            var recorded = record.Steps.FirstOrDefault(s => s.StepIndex == i);
            if (recorded is null)
            {
                return i;
            }
            var cleanPass = recorded.Status is "passed" or "approved" or "skipped";
            if (!cleanPass || !string.Equals(recorded.ParamHash, step.ParamHash, StringComparison.Ordinal))
            {
                return i;
            }
        }
        return playbook.Steps.Count;
    }

    private static string DefaultArtifactDir(Guid pipelineId)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "research-artifacts", pipelineId.ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>The terminal (or parked) outcome of an executor run.</summary>
public sealed record PipelineResult(Guid PipelineId, PipelineResultKind Kind, int? StepIndex, string? VerdictJson)
{
    public static PipelineResult Completed(Guid id) => new(id, PipelineResultKind.Completed, null, null);

    public static PipelineResult Failed(Guid id, int stepIndex, string verdict) => new(id, PipelineResultKind.Failed, stepIndex, verdict);

    public static PipelineResult AwaitingOwner(Guid id, int stepIndex) => new(id, PipelineResultKind.AwaitingOwner, stepIndex, null);

    public Verdict ToVerdict() => Kind switch
    {
        PipelineResultKind.Completed => Verdict.Passing(
            VerdictField.Of("pipeline", PipelineId.ToString()),
            VerdictField.Of("status", "completed")),
        PipelineResultKind.AwaitingOwner => Verdict.Failing(
            VerdictField.Of("pipeline", PipelineId.ToString()),
            VerdictField.Of("status", "awaiting-owner"),
            VerdictField.Of("step", StepIndex ?? -1)),
        _ => Verdict.Failing(
            VerdictField.Of("pipeline", PipelineId.ToString()),
            VerdictField.Of("status", "failed"),
            VerdictField.Of("step", StepIndex ?? -1)),
    };
}

public enum PipelineResultKind
{
    Completed,
    Failed,
    AwaitingOwner,
}

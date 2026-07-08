namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.2 (Q6) — the seam the <see cref="PlaybookExecutor"/> uses to PERSIST pipeline state, backed by the
/// <c>/api/research/pipelines</c> endpoints (Q3 — the CLI never touches the DB directly; the running app
/// owns it). A fake implements this in-memory for unit tests. Every method is idempotent-friendly so a
/// resumed pipeline re-reads state and continues without duplicating rows.
/// </summary>
public interface IPipelineStore
{
    /// <summary>Create a pipeline row with one pending step per playbook step; returns the new id + steps.</summary>
    Task<PipelineRecord> CreateAsync(Playbook playbook, string playbookJson, string? artifactDir, CancellationToken ct);

    /// <summary>Load an existing pipeline (for <c>--resume</c>).</summary>
    Task<PipelineRecord> GetAsync(Guid id, CancellationToken ct);

    Task SetPipelineStatusAsync(Guid id, string status, int currentStepIndex, bool completed, CancellationToken ct);

    Task SetStepStatusAsync(Guid id, int stepIndex, string status, string? verdictJson, string? artifactPath, string paramHash, CancellationToken ct);
}

/// <summary>A pipeline's persisted state as the executor sees it.</summary>
public sealed record PipelineRecord(Guid Id, string Name, string Status, IReadOnlyList<PipelineStepRecord> Steps);

public sealed record PipelineStepRecord(int StepIndex, string Kind, string Status, string ParamHash, string? VerdictJson);

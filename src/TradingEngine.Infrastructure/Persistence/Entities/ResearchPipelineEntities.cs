namespace TradingEngine.Infrastructure.Persistence.Entities;

/// <summary>
/// P3.2 (Q6) — persistent state of a research pipeline run. A pipeline is a playbook (an ordered list of
/// typed steps) executed by <c>TradingEngine.ResearchCli</c> against the running Web app; its state lives
/// here so a run is RESUMABLE by <see cref="Id"/> and REVIEWABLE in the UI (P3.3). Artifacts (reports,
/// captured JSON) live on disk under <c>docs/research/pipelines/{Id}/</c> — this row points at them.
///
/// The engine is a dumb sequential executor (PLAN §12): no DAGs, no parallelism, no retry policies —
/// resumability + honest persisted verdicts are the whole value. <see cref="Status"/> ∈ running |
/// awaiting-owner | completed | failed | cancelled (an <c>owner-gate</c> step parks the pipeline at
/// awaiting-owner until approved in the UI or via <c>research pipeline approve</c>).
/// </summary>
public sealed class ResearchPipelineEntity : IAuditableEntity
{
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public Guid Id { get; set; }

    /// <summary>Human-facing name from the playbook file (e.g. "venue-parity").</summary>
    public string Name { get; set; } = "";

    /// <summary>The verbatim playbook JSON this pipeline is executing — the resume source of truth.</summary>
    public string PlaybookJson { get; set; } = "{}";

    /// <summary>running | awaiting-owner | completed | failed | cancelled.</summary>
    public string Status { get; set; } = "running";

    /// <summary>Zero-based index of the step currently running / awaiting (−1 before start, Count when done).</summary>
    public int CurrentStepIndex { get; set; }

    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Disk folder holding this pipeline's artifacts (docs/research/pipelines/{Id}/).</summary>
    public string? ArtifactDir { get; set; }

    public List<ResearchPipelineStepEntity> Steps { get; set; } = [];
}

/// <summary>
/// P3.2 (Q6) — one executed (or pending) step of a <see cref="ResearchPipelineEntity"/>. The recorded
/// <see cref="VerdictJson"/> is what <c>--resume</c> reads to skip already-passed steps; the
/// <see cref="ParamHash"/> is content-addressed on the step params so a changed param invalidates this
/// step and everything after it (PLAN §6 P3.2). Never rewritten in place across resumes unless re-run.
/// </summary>
public sealed class ResearchPipelineStepEntity : IAuditableEntity
{
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public Guid Id { get; set; }
    public Guid PipelineId { get; set; }

    /// <summary>Position in the playbook (0-based). Unique within a pipeline.</summary>
    public int StepIndex { get; set; }

    /// <summary>Typed step kind: ensure-data | start-run | await-run | assert-gates | reconcile |
    /// exitlab-eval | walk-forward | apply-calibration | owner-gate | report.</summary>
    public string Kind { get; set; } = "";

    /// <summary>pending | running | passed | failed | awaiting-owner | approved | rejected | skipped.</summary>
    public string Status { get; set; } = "pending";

    /// <summary>Content hash of this step's params (resume invalidation key).</summary>
    public string ParamHash { get; set; } = "";

    /// <summary>The machine VERDICT + any captured facts for this step (null until executed).</summary>
    public string? VerdictJson { get; set; }

    /// <summary>Relative path of the artifact this step produced, if any (e.g. reconcile.json).</summary>
    public string? ArtifactPath { get; set; }

    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

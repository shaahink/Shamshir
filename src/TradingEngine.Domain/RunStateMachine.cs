namespace TradingEngine.Domain;

/// <summary>
/// P2.1 (F8) — the single, pure source of truth for legal run-lifecycle transitions.
///
/// The audited F8 finding: the orchestrator's run status was a stringly-typed, multi-writer property
/// (<c>state.Status = "running"</c> scattered across the happy path, the cancel path, the
/// OperationCanceled path and the exception path) with NO enforcement of legal ordering and NO tests.
/// Cancelling was brittle and "stuck running forever" was a whole class of bug. This machine enumerates
/// the states and forbids illegal jumps in ONE place; the orchestrator routes every status write through
/// it so a lifecycle guard can never be bypassed by a stray assignment.
///
/// Vocabulary is shared with <see cref="RunStatusResolver"/> (Q5): <c>completed</c> /
/// <c>completed-with-warnings</c> / <c>failed</c> / <c>cancelled</c> are the persisted terminal strings.
/// <c>queued</c> (P2.2 cTrader queue), <c>starting</c> and <c>finalizing</c> are transient, in-memory
/// only — they are NEVER written to a terminal DB row (a persisted row is always one of the four
/// terminals, so <see cref="RunStatusResolver"/> never has to interpret them).
/// </summary>
public static class RunStateMachine
{
    public const string Queued = "queued";
    public const string Starting = "starting";
    public const string Running = RunStatusResolver.Running;
    public const string Finalizing = "finalizing";
    public const string Completed = RunStatusResolver.Completed;
    public const string CompletedWithWarnings = RunStatusResolver.CompletedWithWarnings;
    public const string Cancelled = RunStatusResolver.Cancelled;
    public const string Failed = RunStatusResolver.Failed;

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Transitions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [Queued] = new HashSet<string>(StringComparer.Ordinal) { Starting, Cancelled, Failed },
            [Starting] = new HashSet<string>(StringComparer.Ordinal) { Running, Finalizing, Cancelled, Failed },
            [Running] = new HashSet<string>(StringComparer.Ordinal) { Finalizing, Cancelled, Failed },
            [Finalizing] = new HashSet<string>(StringComparer.Ordinal) { Completed, CompletedWithWarnings, Cancelled, Failed },
            [Completed] = new HashSet<string>(StringComparer.Ordinal),
            [CompletedWithWarnings] = new HashSet<string>(StringComparer.Ordinal),
            [Cancelled] = new HashSet<string>(StringComparer.Ordinal),
            [Failed] = new HashSet<string>(StringComparer.Ordinal),
        };

    /// <summary>The four persisted terminal states. No transition leaves a terminal state.</summary>
    public static readonly IReadOnlySet<string> TerminalStates =
        new HashSet<string>(StringComparer.Ordinal) { Completed, CompletedWithWarnings, Cancelled, Failed };

    /// <summary>Every state the machine knows about (transient + terminal).</summary>
    public static IReadOnlyCollection<string> AllStates => (IReadOnlyCollection<string>)Transitions.Keys;

    public static bool IsKnown(string state) => Transitions.ContainsKey(state);

    public static bool IsTerminal(string state) => TerminalStates.Contains(state);

    /// <summary>
    /// True iff moving <paramref name="from"/> → <paramref name="to"/> is a legal lifecycle edge.
    /// A no-op self-transition and any move out of a terminal state are NOT legal (callers treat the
    /// latter as an idempotent no-op — e.g. double-cancel — rather than an error).
    /// </summary>
    public static bool CanTransition(string from, string to) =>
        Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    /// <summary>
    /// Attempt a transition. Returns true and echoes <paramref name="to"/> when legal; false with the
    /// unchanged <paramref name="from"/> when illegal (including any move out of a terminal state and
    /// unknown states). Never throws — a lifecycle guard must not be able to crash a run.
    /// </summary>
    public static bool TryTransition(string from, string to, out string resolved)
    {
        if (CanTransition(from, to))
        {
            resolved = to;
            return true;
        }
        resolved = from;
        return false;
    }

    /// <summary>The three ways a guarded writer must treat an attempted status write.</summary>
    public enum TransitionKind
    {
        /// <summary>A legal lifecycle edge — perform the write.</summary>
        Legal,

        /// <summary>
        /// A benign no-op that changes nothing and is NOT an ordering bug: re-entering the current state
        /// (e.g. an <c>OperationCanceled</c> that lands while already <c>finalizing</c>) or any attempt to
        /// leave a terminal state (double-cancel, teardown after completion). Skip the write silently.
        /// </summary>
        IdempotentNoOp,

        /// <summary>
        /// A genuine ordering violation from a live non-terminal state (e.g. <c>running→starting</c>, or a
        /// jump to/from an unknown state). The guarded writer logs + journals this as a real bug to fix.
        /// </summary>
        Illegal,
    }

    /// <summary>
    /// Classify an attempted write so a single guarded writer can tell an idempotent no-op (double-cancel,
    /// re-entering the current state) apart from a genuine ordering violation. This is the semantics the
    /// orchestrator's <c>TransitionRun</c> needs: only <see cref="TransitionKind.Illegal"/> deserves a
    /// warning + a <c>LIFECYCLE</c> journal row — treating a harmless self-transition as "illegal" would
    /// pollute the very audit trail that flag is meant to reserve for real bugs. Pure + never throws.
    /// </summary>
    public static TransitionKind Classify(string from, string to)
    {
        if (CanTransition(from, to))
            return TransitionKind.Legal;

        // A write that changes nothing is never an ordering bug, even from an unknown state.
        if (string.Equals(from, to, StringComparison.Ordinal))
            return TransitionKind.IdempotentNoOp;

        // Leaving a terminal state is the sanctioned double-cancel / post-completion-teardown no-op.
        if (IsTerminal(from))
            return TransitionKind.IdempotentNoOp;

        return TransitionKind.Illegal;
    }
}

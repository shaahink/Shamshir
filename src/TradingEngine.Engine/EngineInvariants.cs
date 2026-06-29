using TradingEngine.Domain;

namespace TradingEngine.Engine;

/// <summary>
/// iter-redesign P1.4 — machine-checkable invariants over <see cref="EngineState"/> that lock the
/// engine-truth guarantees from Phase 1 permanently. Cheap enough to run after every bar in tests
/// (and in debug builds), so the "leaked open book" class of bug (§1.1) can never regress silently.
///
/// The core invariant: <b>the live book contains only live positions.</b> A position that has reached a
/// terminal phase (Closed/Rejected/Cancelled) must never remain in <see cref="EngineState.Positions"/>,
/// because the pre-trade gate sums that dictionary for exposure/heat/position-count — a single retained
/// terminal position permanently inflates <c>totalOpenRisk</c> and latches every later proposal into
/// BudgetBlocked/MAX_*.
/// </summary>
public static class EngineInvariants
{
    public readonly record struct Violation(string Rule, string Detail);

    /// <summary>Returns all invariant violations for <paramref name="state"/> (empty = healthy).</summary>
    public static IReadOnlyList<Violation> Inspect(EngineState state)
    {
        var violations = new List<Violation>();

        foreach (var (id, pos) in state.Positions)
        {
            if (pos.Phase is PositionPhase.Closed or PositionPhase.Rejected or PositionPhase.Cancelled)
            {
                violations.Add(new Violation(
                    "NoTerminalPositionsRetained",
                    $"position {id} is in terminal phase {pos.Phase} but is still in the live book"));
            }
        }

        return violations;
    }

    /// <summary>True when <paramref name="state"/> satisfies every invariant.</summary>
    public static bool IsHealthy(EngineState state) => Inspect(state).Count == 0;

    /// <summary>
    /// Throws <see cref="EngineInvariantViolationException"/> if any invariant is violated. Use in tests /
    /// debug builds after each bar so a leak fails loudly at the exact step that introduced it.
    /// </summary>
    public static void Check(EngineState state)
    {
        var violations = Inspect(state);
        if (violations.Count > 0)
        {
            throw new EngineInvariantViolationException(violations);
        }
    }
}

public sealed class EngineInvariantViolationException : Exception
{
    public IReadOnlyList<EngineInvariants.Violation> Violations { get; }

    public EngineInvariantViolationException(IReadOnlyList<EngineInvariants.Violation> violations)
        : base("Engine invariant(s) violated: " + string.Join("; ", violations.Select(v => $"[{v.Rule}] {v.Detail}")))
    {
        Violations = violations;
    }
}

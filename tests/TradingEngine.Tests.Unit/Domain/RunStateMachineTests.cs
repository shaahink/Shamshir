using TradingEngine.Domain;

namespace TradingEngine.Tests.Unit.Domain;

// P2.1 (F8) — the audited F8 finding was that the run lifecycle was a stringly-typed, multi-writer
// status with NO enforcement of legal ordering and NO tests; cancel was brittle and "stuck running
// forever" was a bug class. RunStateMachine is the ONE place that forbids illegal jumps. These are the
// state-machine tests F8 says never existed — every legal edge, representative illegal jumps, terminal
// idempotency (double-cancel), and cancel-during-finalize.
[Trait("Category", "Domain")]
public sealed class RunStateMachineTests
{
    [Theory]
    // queued (P2.2 queue) → start | reject before start | fail before start
    [InlineData(RunStateMachine.Queued, RunStateMachine.Starting)]
    [InlineData(RunStateMachine.Queued, RunStateMachine.Cancelled)]
    [InlineData(RunStateMachine.Queued, RunStateMachine.Failed)]
    // starting → running (normal) | finalizing (empty run) | cancelled | failed
    [InlineData(RunStateMachine.Starting, RunStateMachine.Running)]
    [InlineData(RunStateMachine.Starting, RunStateMachine.Finalizing)]
    [InlineData(RunStateMachine.Starting, RunStateMachine.Cancelled)]
    [InlineData(RunStateMachine.Starting, RunStateMachine.Failed)]
    // running → finalizing (happy) | cancelled (mid-run cancel) | failed (mid-run fault)
    [InlineData(RunStateMachine.Running, RunStateMachine.Finalizing)]
    [InlineData(RunStateMachine.Running, RunStateMachine.Cancelled)]
    [InlineData(RunStateMachine.Running, RunStateMachine.Failed)]
    // finalizing → each terminal (incl. cancel-during-finalize)
    [InlineData(RunStateMachine.Finalizing, RunStateMachine.Completed)]
    [InlineData(RunStateMachine.Finalizing, RunStateMachine.CompletedWithWarnings)]
    [InlineData(RunStateMachine.Finalizing, RunStateMachine.Cancelled)]
    [InlineData(RunStateMachine.Finalizing, RunStateMachine.Failed)]
    public void LegalTransitions_AreAllowed(string from, string to)
    {
        RunStateMachine.CanTransition(from, to).Should().BeTrue($"{from}->{to} is a legal lifecycle edge");
        RunStateMachine.TryTransition(from, to, out var resolved).Should().BeTrue();
        resolved.Should().Be(to);
    }

    [Theory]
    // The happy path MUST go through finalizing — a direct running->completed jump is forbidden so the
    // barrier/stats/end-record step can never be skipped.
    [InlineData(RunStateMachine.Running, RunStateMachine.Completed)]
    [InlineData(RunStateMachine.Running, RunStateMachine.CompletedWithWarnings)]
    // Can't resurrect: queued/starting can't jump straight to a completed result.
    [InlineData(RunStateMachine.Queued, RunStateMachine.Running)]
    [InlineData(RunStateMachine.Queued, RunStateMachine.Completed)]
    [InlineData(RunStateMachine.Starting, RunStateMachine.Completed)]
    // Backwards moves are illegal.
    [InlineData(RunStateMachine.Running, RunStateMachine.Starting)]
    [InlineData(RunStateMachine.Finalizing, RunStateMachine.Running)]
    // Unknown states.
    [InlineData("bogus", RunStateMachine.Running)]
    [InlineData(RunStateMachine.Running, "bogus")]
    public void IllegalTransitions_AreRejected_WithoutThrowing(string from, string to)
    {
        RunStateMachine.CanTransition(from, to).Should().BeFalse($"{from}->{to} is not a legal edge");
        RunStateMachine.TryTransition(from, to, out var resolved).Should().BeFalse();
        resolved.Should().Be(from, "an illegal transition leaves the status unchanged");
    }

    [Theory]
    [InlineData(RunStateMachine.Completed)]
    [InlineData(RunStateMachine.CompletedWithWarnings)]
    [InlineData(RunStateMachine.Cancelled)]
    [InlineData(RunStateMachine.Failed)]
    public void TerminalStates_HaveNoOutgoingTransitions(string terminal)
    {
        RunStateMachine.IsTerminal(terminal).Should().BeTrue();
        foreach (var target in RunStateMachine.AllStates)
        {
            RunStateMachine.CanTransition(terminal, target)
                .Should().BeFalse($"a terminal state ({terminal}) can never leave itself (target {target})");
        }
    }

    [Fact]
    public void DoubleCancel_IsIdempotentNoOp()
    {
        // First cancel: running -> cancelled is legal.
        RunStateMachine.TryTransition(RunStateMachine.Running, RunStateMachine.Cancelled, out var first).Should().BeTrue();
        first.Should().Be(RunStateMachine.Cancelled);

        // Second cancel: cancelled -> cancelled is rejected (no leaving a terminal); the caller treats
        // this false as a benign no-op so a double-cancel never corrupts a finished run.
        RunStateMachine.TryTransition(RunStateMachine.Cancelled, RunStateMachine.Cancelled, out var second).Should().BeFalse();
        second.Should().Be(RunStateMachine.Cancelled);
    }

    [Fact]
    public void CancelDuringFinalize_IsLegal()
    {
        // A cancel that lands while the run is finalizing must still resolve to cancelled.
        RunStateMachine.CanTransition(RunStateMachine.Finalizing, RunStateMachine.Cancelled).Should().BeTrue();
    }

    [Fact]
    public void SelfTransition_IsNotLegal()
    {
        foreach (var s in RunStateMachine.AllStates)
        {
            RunStateMachine.CanTransition(s, s).Should().BeFalse($"{s}->{s} self-loop is not a lifecycle edge");
        }
    }

    [Fact]
    public void KnownStates_CoverTheDocumentedVocabulary()
    {
        var expected = new[]
        {
            RunStateMachine.Queued, RunStateMachine.Starting, RunStateMachine.Running,
            RunStateMachine.Finalizing, RunStateMachine.Completed, RunStateMachine.CompletedWithWarnings,
            RunStateMachine.Cancelled, RunStateMachine.Failed,
        };
        RunStateMachine.AllStates.Should().BeEquivalentTo(expected);
        foreach (var s in expected)
        {
            RunStateMachine.IsKnown(s).Should().BeTrue();
        }
        RunStateMachine.IsKnown("bogus").Should().BeFalse();
    }

    [Fact]
    public void SharesVocabularyWith_RunStatusResolver()
    {
        // The persisted terminal strings must be identical to RunStatusResolver's (one vocabulary, Q5).
        RunStateMachine.Running.Should().Be(RunStatusResolver.Running);
        RunStateMachine.Completed.Should().Be(RunStatusResolver.Completed);
        RunStateMachine.CompletedWithWarnings.Should().Be(RunStatusResolver.CompletedWithWarnings);
        RunStateMachine.Cancelled.Should().Be(RunStatusResolver.Cancelled);
        RunStateMachine.Failed.Should().Be(RunStatusResolver.Failed);
    }

    // --- Classify: the guarded writer must tell an idempotent no-op apart from a real ordering bug so
    // the LIFECYCLE journal flag stays reserved for genuine violations (audit-trail integrity). ---

    [Theory]
    [InlineData(RunStateMachine.Queued, RunStateMachine.Starting)]
    [InlineData(RunStateMachine.Starting, RunStateMachine.Running)]
    [InlineData(RunStateMachine.Running, RunStateMachine.Finalizing)]
    [InlineData(RunStateMachine.Finalizing, RunStateMachine.Completed)]
    [InlineData(RunStateMachine.Finalizing, RunStateMachine.Cancelled)]
    public void Classify_LegalEdge_IsLegal(string from, string to) =>
        RunStateMachine.Classify(from, to).Should().Be(RunStateMachine.TransitionKind.Legal);

    [Theory]
    // The exact bug this fixes: an OperationCanceled that lands while already finalizing re-enters
    // finalizing — that must be a benign no-op, NOT a false "illegal transition" warning + journal row.
    [InlineData(RunStateMachine.Finalizing, RunStateMachine.Finalizing)]
    [InlineData(RunStateMachine.Running, RunStateMachine.Running)]
    [InlineData(RunStateMachine.Starting, RunStateMachine.Starting)]
    // Leaving a terminal is the sanctioned double-cancel / post-completion-teardown no-op.
    [InlineData(RunStateMachine.Cancelled, RunStateMachine.Cancelled)]
    [InlineData(RunStateMachine.Completed, RunStateMachine.Completed)]
    [InlineData(RunStateMachine.Completed, RunStateMachine.Failed)]
    [InlineData(RunStateMachine.Cancelled, RunStateMachine.Completed)]
    [InlineData(RunStateMachine.Failed, RunStateMachine.Running)]
    public void Classify_NoOp_ForSelfTransitionOrLeavingTerminal(string from, string to) =>
        RunStateMachine.Classify(from, to).Should().Be(RunStateMachine.TransitionKind.IdempotentNoOp);

    [Theory]
    // A genuine ordering violation from a live non-terminal state — the ONLY thing that should warn+journal.
    [InlineData(RunStateMachine.Running, RunStateMachine.Completed)]
    [InlineData(RunStateMachine.Running, RunStateMachine.Starting)]
    [InlineData(RunStateMachine.Queued, RunStateMachine.Completed)]
    [InlineData(RunStateMachine.Finalizing, RunStateMachine.Running)]
    // Jumps involving an unknown state (that actually change the value) are illegal, not benign.
    [InlineData("bogus", RunStateMachine.Running)]
    [InlineData(RunStateMachine.Running, "bogus")]
    public void Classify_GenuineOrderingViolation_IsIllegal(string from, string to) =>
        RunStateMachine.Classify(from, to).Should().Be(RunStateMachine.TransitionKind.Illegal);

    [Fact]
    public void Classify_NeverContradicts_CanTransition()
    {
        // Every state pair: Classify==Legal iff CanTransition==true (the two agree on legality; Classify
        // only adds the no-op/illegal split on top of the rejected set).
        var universe = RunStateMachine.AllStates.Append("bogus").ToArray();
        foreach (var from in universe)
        {
            foreach (var to in universe)
            {
                var legal = RunStateMachine.Classify(from, to) == RunStateMachine.TransitionKind.Legal;
                legal.Should().Be(RunStateMachine.CanTransition(from, to), $"{from}->{to}");
            }
        }
    }
}

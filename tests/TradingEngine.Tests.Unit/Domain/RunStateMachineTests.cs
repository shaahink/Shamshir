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
}

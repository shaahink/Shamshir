namespace TradingEngine.Tests.Unit.Domain;

// P0.2 (F5, Q5) — run-status vocabulary. Before this, four readers each derived status inline as
// `ErrorMessage != null ? failed : completed`, conflating an engine-result failure with a
// transport/persistence teardown fault — so a fully-complete cTrader run whose NetMQ teardown threw was
// stamped `failed` (the audited F5 bug). RunStatusResolver is the single source of truth: `failed` is
// reserved for "no trustworthy result"; a complete result with a teardown anomaly is
// `completed-with-warnings`.
[Trait("Category", "Domain")]
public sealed class RunStatusResolverTests
{
    [Fact]
    public void CleanCompletedRun_IsCompleted()
    {
        RunStatusResolver.Resolve(isCompleted: true, errorMessage: null, warningsJson: null)
            .Should().Be("completed");
    }

    [Fact]
    public void CompletedWithWarnings_WhenWarningsPresent_AndNoError()
    {
        // The F5 scenario: engine produced a complete result; teardown attached a warning. Must NOT be failed.
        var warnings = """[{"code":"HOST_DISPOSE","detail":"NetMQPoller disposed","atUtc":"2026-07-08T00:00:00Z"}]""";
        RunStatusResolver.Resolve(isCompleted: true, errorMessage: null, warningsJson: warnings)
            .Should().Be("completed-with-warnings");
    }

    [Fact]
    public void Failed_WhenErrorMessageSet_EvenWithWarnings()
    {
        // No trustworthy result (ErrorMessage set) => failed, regardless of any warnings.
        RunStatusResolver.Resolve(isCompleted: true, errorMessage: "No bars found", warningsJson: "[{\"code\":\"X\"}]")
            .Should().Be("failed");
    }

    [Fact]
    public void Running_WhenNotCompleted_AndNotStuck()
    {
        RunStatusResolver.Resolve(isCompleted: false, errorMessage: null, warningsJson: null, isStuck: false)
            .Should().Be("running");
    }

    [Fact]
    public void Failed_WhenNotCompleted_ButStuck()
    {
        RunStatusResolver.Resolve(isCompleted: false, errorMessage: null, warningsJson: null, isStuck: true)
            .Should().Be("failed");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("[]", false)]
    [InlineData("{}", false)]
    [InlineData("null", false)]
    [InlineData("  ", false)]
    [InlineData("[{\"code\":\"X\"}]", true)]
    public void HasWarnings_TreatsEmptyContainersAsNoWarnings(string? json, bool expected)
    {
        RunStatusResolver.HasWarnings(json).Should().Be(expected);
    }
}

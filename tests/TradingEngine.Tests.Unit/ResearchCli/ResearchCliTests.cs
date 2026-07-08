using TradingEngine.ResearchCli;

namespace TradingEngine.Tests.Unit.ResearchCli;

// P3.1 — the pipeline agent (P3.2) and a human both branch on the ResearchCli's pure decisions: the
// machine `VERDICT:` line, the run-validate gate check, the arg grammar, and tolerant run-JSON parsing.
// These are the credential-free units behind the HTTP shell; the driving surface un-sticks the agent
// only if they are exact and deterministic (R6 — agents die driving Angular, not parsing a verdict).
[Trait("Category", "Domain")]
public sealed class ResearchCliTests
{
    [Fact]
    public void Verdict_Pass_RendersCanonicalLine_AndExitZero()
    {
        var v = Verdict.Passing(VerdictField.Of("status", "completed"), VerdictField.Of("trades", 7));
        v.Render().Should().Be("VERDICT: PASS status=completed trades=7");
        v.ExitCode.Should().Be(0);
    }

    [Fact]
    public void Verdict_Fail_RendersFailHead_AndNonZeroExit()
    {
        var v = Verdict.Failing(VerdictField.Of("error", "missing-runId"));
        v.Render().Should().Be("VERDICT: FAIL error=missing-runId");
        v.ExitCode.Should().Be(1);
    }

    [Fact]
    public void Verdict_QuotesValuesWithSpaces_SoGrammarStaysSplittable()
    {
        var v = Verdict.Failing(VerdictField.Of("failed", "status!=completed trades<1"));
        v.Render().Should().Be("VERDICT: FAIL failed=\"status!=completed trades<1\"");
    }

    [Fact]
    public void Gate_AllPass_WhenCompletedWithTradesAndNoWarnings()
    {
        var run = new RunGateInput("completed", 5, null, null);
        var gates = new GateSpec { RequireStatus = "completed", MinTrades = 1, ForbidWarnings = true };
        GateEvaluator.Evaluate(run, gates).Pass.Should().BeTrue();
    }

    [Fact]
    public void Gate_Fails_OnWrongStatus_ZeroTrades_AndListsEveryReason()
    {
        var run = new RunGateInput("failed", 0, null, "No bars found");
        var gates = new GateSpec { RequireStatus = "completed", MinTrades = 1 };
        var v = GateEvaluator.Evaluate(run, gates);
        v.Pass.Should().BeFalse();
        var line = v.Render();
        line.Should().Contain("status!=completed");
        line.Should().Contain("trades<1");
    }

    [Fact]
    public void Gate_Fails_WhenForbiddenWarningCodePresent()
    {
        var warnings = """[{"code":"TRADES_LOST","detail":"12:0"}]""";
        var run = new RunGateInput("completed-with-warnings", 0, warnings, null);
        var gates = new GateSpec { MinTrades = 0, ForbidWarningCodes = ["TRADES_LOST"] };
        var v = GateEvaluator.Evaluate(run, gates);
        v.Pass.Should().BeFalse();
        v.Render().Should().Contain("warning:TRADES_LOST");
    }

    [Fact]
    public void Gate_ForbidWarnings_FailsForCompletedWithWarnings_ButPassesClean()
    {
        var warned = new RunGateInput("completed-with-warnings", 3, """[{"code":"HOST_DISPOSE"}]""", null);
        GateEvaluator.Evaluate(warned, new GateSpec { ForbidWarnings = true }).Pass.Should().BeFalse();

        var clean = new RunGateInput("completed", 3, "[]", null);
        GateEvaluator.Evaluate(clean, new GateSpec { ForbidWarnings = true }).Pass.Should().BeTrue();
    }

    [Fact]
    public void Args_ParsesVerbPath_OptionsWithValues_AndFlags()
    {
        var a = CliArgs.Parse(["run", "validate", "abc123", "--require-status", "completed", "--min-trades", "1", "--json"]);
        a.Verb.Should().Be("run validate");
        a.Positionals.Should().ContainInOrder("run", "validate", "abc123");
        a.Option("require-status").Should().Be("completed");
        a.Option("min-trades", 0).Should().Be(1);
        a.Flag("json").Should().BeTrue();
        a.Flag("nope").Should().BeFalse();
    }

    [Fact]
    public void Args_SupportsEqualsForm_AndFallbacks()
    {
        var a = CliArgs.Parse(["reconcile", "--left=aaa", "--right=bbb", "--base-url=https://x"]);
        a.Verb.Should().Be("reconcile");
        a.Option("left").Should().Be("aaa");
        a.Option("right").Should().Be("bbb");
        a.Option("base-url", "def").Should().Be("https://x");
        a.Option("missing", "def").Should().Be("def");
        a.Option("missing-int", 42).Should().Be(42);
    }

    [Fact]
    public void RunJson_ParsesCamelCaseDetail_Tolerantly()
    {
        var json = """
            {"runId":"abc123","status":"completed-with-warnings","totalTrades":7,
             "errorMessage":null,"warningsJson":"[{\"code\":\"HOST_DISPOSE\"}]"}
            """;
        var run = RunJson.ParseRun(json);
        run.Status.Should().Be("completed-with-warnings");
        run.TotalTrades.Should().Be(7);
        run.ErrorMessage.Should().BeNull();
        run.WarningsJson.Should().Contain("HOST_DISPOSE");
    }

    [Fact]
    public void RunJson_MissingFields_DegradeToSafeDefaults()
    {
        var run = RunJson.ParseRun("""{"foo":"bar"}""");
        run.Status.Should().Be("unknown");
        run.TotalTrades.Should().Be(0);
        run.WarningsJson.Should().BeNull();
    }
}

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

    // --- P3.1 finish: data ensure / run start / exitlab / walkforward pure helpers ---

    [Fact]
    public void Coverage_FlagsMissingCell_WhenSymbolTimeframeAbsent()
    {
        var inv = InventoryCoverage.ParseInventory("""
            [{"symbol":"EURUSD","timeframe":"H1","barCount":5000,
              "firstBar":"2026-01-01T00:00:00Z","lastBar":"2026-07-01T00:00:00Z"}]
            """);
        var cells = InventoryCoverage.Evaluate(inv, ["EURUSD", "XAUUSD"], ["H1"], null, null);
        cells.Should().HaveCount(2);
        cells.Single(c => c.Symbol == "EURUSD").Satisfied.Should().BeTrue();
        var missing = InventoryCoverage.Missing(cells);
        missing.Should().ContainSingle(c => c.Symbol == "XAUUSD" && !c.Present);
    }

    [Fact]
    public void Coverage_FailsRange_WhenInventoryDoesNotSpanRequestedWindow()
    {
        var inv = InventoryCoverage.ParseInventory("""
            [{"symbol":"EURUSD","timeframe":"H1","barCount":100,
              "firstBar":"2026-03-01T00:00:00Z","lastBar":"2026-04-01T00:00:00Z"}]
            """);
        var cells = InventoryCoverage.Evaluate(inv, ["eurusd"], ["h1"],
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        cells.Single().Satisfied.Should().BeFalse();
        cells.Single().Present.Should().BeTrue();
        cells.Single().CoversRange.Should().BeFalse();
    }

    [Fact]
    public void Coverage_ZeroBars_IsNotSatisfied_EvenWhenPresent()
    {
        var inv = InventoryCoverage.ParseInventory("""
            [{"symbol":"EURUSD","timeframe":"H1","barCount":0,"firstBar":null,"lastBar":null}]
            """);
        var cells = InventoryCoverage.Evaluate(inv, ["EURUSD"], ["H1"], null, null);
        cells.Single().Satisfied.Should().BeFalse();
    }

    [Fact]
    public void StartRunPlan_Overrides_WinOverPlanFile()
    {
        var plan = """{"symbols":["EURUSD"],"periods":["H1"],"venue":"replay"}""";
        var body = StartRunPlan.BuildBody(plan, venue: "tape", compareBoth: true, explore: true);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("venue").GetString().Should().Be("tape");
        root.GetProperty("compareBoth").GetBoolean().Should().BeTrue();
        root.GetProperty("explorationMode").GetBoolean().Should().BeTrue();
        root.GetProperty("recordExcursions").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void StartRunPlan_LeavesPlanUntouched_WhenNoOverrides()
    {
        var plan = """{"symbols":["EURUSD"],"venue":"tape"}""";
        var body = StartRunPlan.BuildBody(plan, venue: null, compareBoth: false, explore: false);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("venue").GetString().Should().Be("tape");
        doc.RootElement.TryGetProperty("compareBoth", out _).Should().BeFalse();
    }

    [Fact]
    public void StartRunPlan_Throws_OnNonObjectPlan()
    {
        var act = () => StartRunPlan.BuildBody("[1,2,3]", null, false, false);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void StartRunPlan_ParsesStartResponse()
    {
        var (runId, status) = StartRunPlan.ParseStartResponse("""{"runId":"abc123","status":"starting"}""");
        runId.Should().Be("abc123");
        status.Should().Be("starting");
    }

    [Fact]
    public void ExitLabResult_ParsesSummary_AndJobId()
    {
        var (trades, cells) = ExitLabResult.ParseSummary("""{"totalTrades":42,"totalCells":180,"cells":[]}""");
        trades.Should().Be(42);
        cells.Should().Be(180);
        ExitLabResult.ParseJobId("""{"jobId":"11111111-2222-3333-4444-555555555555","status":"enqueued"}""")
            .Should().Be("11111111-2222-3333-4444-555555555555");
    }
}

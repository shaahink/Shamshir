using System.Text.Json.Nodes;
using TradingEngine.ResearchCli;

namespace TradingEngine.Tests.Unit.ResearchCli;

// P3.2 — the playbook engine is the centerpiece: a dumb sequential executor with persisted verdicts,
// resume-by-content-hash, and owner-gates that PARK. Its whole value is that the pass/fail/park/resume
// control flow is honest and deterministic — so it is pinned here credential-free with fake seams
// (IStepRunner / IPipelineStore), exactly as an agent would depend on it (R6). No app, no DB, no HTTP.
[Trait("Category", "Domain")]
public sealed class PlaybookEngineTests
{
    // ---- parser ----

    [Fact]
    public void Parser_ReadsNameKindsParams_AndSeparatesReservedKeys()
    {
        var pb = PlaybookParser.Parse("""
            {"name":"venue-parity","steps":[
              {"kind":"ensure-data","symbols":"EURUSD","tfs":"H1"},
              {"kind":"start-run","venue":"tape","continueOnFail":true}
            ]}
            """);
        pb.Name.Should().Be("venue-parity");
        pb.Steps.Should().HaveCount(2);
        pb.Steps[0].Kind.Should().Be("ensure-data");
        pb.Steps[0].Params["symbols"]!.GetValue<string>().Should().Be("EURUSD");
        pb.Steps[0].Params.ContainsKey("kind").Should().BeFalse("kind is reserved, not a param");
        pb.Steps[1].ContinueOnFail.Should().BeTrue();
        pb.Steps[1].Params.ContainsKey("continueOnFail").Should().BeFalse();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"steps":[{"kind":"report"}]}""")]              // no name
    [InlineData("""{"name":"x","steps":[]}""")]                    // empty steps
    [InlineData("""{"name":"x","steps":[{"kind":"bogus"}]}""")]    // unknown kind
    public void Parser_RejectsMalformed(string json)
    {
        var act = () => PlaybookParser.Parse(json);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParamHash_IsStableAcrossKeyOrder_ButChangesWithValue()
    {
        var a = new PlaybookStep(0, "start-run", Obj("""{"venue":"tape","symbols":"EURUSD"}"""), false);
        var b = new PlaybookStep(0, "start-run", Obj("""{"symbols":"EURUSD","venue":"tape"}"""), false);
        var c = new PlaybookStep(0, "start-run", Obj("""{"venue":"ctrader","symbols":"EURUSD"}"""), false);
        a.ParamHash.Should().Be(b.ParamHash, "key ordering must not change the content hash");
        a.ParamHash.Should().NotBe(c.ParamHash, "a changed value must change the hash");
    }

    // ---- executor happy path / fail-stop / continueOnFail ----

    [Fact]
    public async Task Executor_RunsAllSteps_AndCompletes()
    {
        var pb = Pb(("ensure-data", false), ("start-run", false), ("report", false));
        var runner = new FakeRunner { Default = StepOutcome.Pass("VERDICT: PASS") };
        var store = new FakeStore();
        var result = await new PlaybookExecutor(runner, store).RunAsync(pb, "{}", null, default);

        result.Kind.Should().Be(PipelineResultKind.Completed);
        store.PipelineStatus.Should().Be("completed");
        store.StepStatus.Values.Should().OnlyContain(s => s == "passed");
        runner.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task Executor_StopsOnFail_ByDefault()
    {
        var pb = Pb(("ensure-data", false), ("start-run", false), ("report", false));
        var runner = new FakeRunner();
        runner.ByIndex[1] = StepOutcome.Fail("VERDICT: FAIL error=x");
        var store = new FakeStore();
        var result = await new PlaybookExecutor(runner, store).RunAsync(pb, "{}", null, default);

        result.Kind.Should().Be(PipelineResultKind.Failed);
        result.StepIndex.Should().Be(1);
        store.PipelineStatus.Should().Be("failed");
        store.StepStatus[2].Should().Be("pending", "the executor must not run steps after a hard fail");
    }

    [Fact]
    public async Task Executor_ContinuesPastFail_WhenContinueOnFail()
    {
        var pb = Pb(("ensure-data", true), ("report", false));
        var runner = new FakeRunner();
        runner.ByIndex[0] = StepOutcome.Fail("VERDICT: FAIL");
        runner.ByIndex[1] = StepOutcome.Pass("VERDICT: PASS");
        var store = new FakeStore();
        var result = await new PlaybookExecutor(runner, store).RunAsync(pb, "{}", null, default);

        result.Kind.Should().Be(PipelineResultKind.Completed);
        store.StepStatus[0].Should().Be("failed");
        store.StepStatus[1].Should().Be("passed");
    }

    [Fact]
    public async Task Executor_ParksOnOwnerGate()
    {
        var pb = Pb(("reconcile", false), ("owner-gate", false), ("apply-calibration", false));
        var runner = new FakeRunner { Default = StepOutcome.Pass("VERDICT: PASS") };
        runner.ByIndex[1] = StepOutcome.AwaitOwner("VERDICT: FAIL gate=owner");
        var store = new FakeStore();
        var result = await new PlaybookExecutor(runner, store).RunAsync(pb, "{}", null, default);

        result.Kind.Should().Be(PipelineResultKind.AwaitingOwner);
        result.StepIndex.Should().Be(1);
        store.PipelineStatus.Should().Be("awaiting-owner");
        store.StepStatus[1].Should().Be("awaiting-owner");
        store.StepStatus[2].Should().Be("pending", "a parked pipeline must not run past the gate");
    }

    [Fact]
    public async Task Executor_CatchesStepException_AsFailedVerdict()
    {
        var pb = Pb(("start-run", false));
        var runner = new FakeRunner { Throw = new InvalidOperationException("boom") };
        var store = new FakeStore();
        var result = await new PlaybookExecutor(runner, store).RunAsync(pb, "{}", null, default);

        result.Kind.Should().Be(PipelineResultKind.Failed);
        store.StepStatus[0].Should().Be("failed");
        store.LastStepVerdict.Should().Contain("InvalidOperationException");
    }

    // ---- resume: content-addressed invalidation ----

    [Fact]
    public void Resume_SkipsPassedSteps_WithUnchangedHash()
    {
        var pb = Pb(("ensure-data", false), ("start-run", false), ("report", false));
        var record = RecordFor(pb, [("passed", true), ("passed", true), ("pending", true)]);
        PlaybookExecutor.FirstInvalidStepIndex(pb, record).Should().Be(2, "first non-passed step re-runs");
    }

    [Fact]
    public void Resume_ReRunsFromChangedParam_Onward()
    {
        var pb = Pb(("ensure-data", false), ("start-run", false), ("report", false));
        // step 1's stored hash no longer matches the playbook (param changed) → re-run from 1.
        var record = RecordFor(pb, [("passed", true), ("passed", false), ("passed", true)]);
        PlaybookExecutor.FirstInvalidStepIndex(pb, record).Should().Be(1);
    }

    [Fact]
    public void Resume_AllValidPasses_ReturnsCount_NothingToDo()
    {
        var pb = Pb(("ensure-data", false), ("report", false));
        var record = RecordFor(pb, [("passed", true), ("approved", true)]);
        PlaybookExecutor.FirstInvalidStepIndex(pb, record).Should().Be(2);
    }

    [Fact]
    public async Task Resume_ContinuesFromParkedGate_AfterApproval()
    {
        var pb = Pb(("reconcile", false), ("owner-gate", false), ("report", false));
        // Gate approved out-of-band; steps 0+1 clean, step 2 pending → resume runs only step 2.
        var record = RecordFor(pb, [("passed", true), ("approved", true), ("pending", true)]);
        var runner = new FakeRunner { Default = StepOutcome.Pass("VERDICT: PASS") };
        var store = new FakeStore(record);
        var result = await new PlaybookExecutor(runner, store).ResumeAsync(pb, record.Id, null, default);

        result.Kind.Should().Be(PipelineResultKind.Completed);
        runner.CallCount.Should().Be(1, "only the pending post-gate step re-runs");
    }

    // ---- shipped canonical playbooks must parse (P3.4) ----

    [Theory]
    [InlineData("venue-parity.json")]
    [InlineData("explore-exit.json")]
    [InlineData("data-quality.json")]
    [InlineData("session-fingerprint.json")]
    [InlineData("spread-vol-filter.json")]
    [InlineData("regime-calibration.json")]
    [InlineData("block-bootstrap.json")]
    [InlineData("meta-allocator.json")]
    [InlineData("entry-quality.json")]
    [InlineData("pyramid-policy.json")]
    public void ShippedPlaybook_Parses_AndHasKnownStepKinds(string file)
    {
        var path = Path.Combine(RepoRoot(), "playbooks", file);
        File.Exists(path).Should().BeTrue($"the canonical playbook {file} must ship in playbooks/");
        var pb = PlaybookParser.Parse(File.ReadAllText(path));
        pb.Steps.Should().NotBeEmpty();
        pb.Steps.Should().OnlyContain(s => StepKinds.IsKnown(s.Kind));
    }

    // P6.4: the regime-calibration playbook must ship with per-regime exitlab-eval steps
    // that carry the optional "regime" parameter for session-based exit-rule calibration.
    [Fact]
    public void RegimePlaybook_HasPerRegimeExitLabSteps()
    {
        var path = Path.Combine(RepoRoot(), "playbooks", "regime-calibration.json");
        var pb = PlaybookParser.Parse(File.ReadAllText(path));

        // Steps 4-7 should be exitlab-eval with increasing regime specificity
        var evalSteps = pb.Steps.Where(s => s.Kind == StepKinds.ExitLabEval).ToList();
        evalSteps.Should().HaveCount(4, "regime playbook evaluates all-sessions + 3 per-regime");

        // Step 4 (index 4): all-sessions (no regime filter)
        evalSteps[0].Params.ContainsKey("regime").Should().BeFalse("first eval is pooled (no regime filter)");

        // Steps 5-7 (indices 5-7): per-regime with explicit "regime" key
        evalSteps[1].Params["regime"]!.GetValue<string>().Should().Be("London-NY");
        evalSteps[2].Params["regime"]!.GetValue<string>().Should().Be("London");
        evalSteps[3].Params["regime"]!.GetValue<string>().Should().Be("Asian");

        // The apply-calibration step must carry a regime key
        var calStep = pb.Steps.Single(s => s.Kind == StepKinds.ApplyCalibration);
        calStep.Params["regime"]!.GetValue<string>().Should().Be("London-NY");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "playbooks")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }
        return dir ?? throw new InvalidOperationException("Could not locate repo root (playbooks) from test output dir.");
    }

    // ---- artifact dir auto-creation ----

    [Fact]
    public async Task Executor_AutoCreatesArtifactDir_WhenNoneGiven()
    {
        var pb = Pb(("report", false));
        var runner = new FakeRunner { Default = StepOutcome.Pass("VERDICT: PASS") };
        var store = new FakeStore();
        var result = await new PlaybookExecutor(runner, store).RunAsync(pb, "{}", null, default);

        result.Kind.Should().Be(PipelineResultKind.Completed);
        runner.LastContext.Should().NotBeNull();
        runner.LastContext!.ArtifactDir.Should().NotBeNull();
        runner.LastContext!.ArtifactDir.Should().Contain("research-artifacts");
        Directory.Exists(runner.LastContext!.ArtifactDir).Should().BeTrue();
    }

    [Fact]
    public async Task Executor_UsesUserSuppliedArtifactDir_WhenGiven()
    {
        var pb = Pb(("report", false));
        var runner = new FakeRunner { Default = StepOutcome.Pass("VERDICT: PASS") };
        var store = new FakeStore();
        var tmp = Path.Combine(Path.GetTempPath(), "shamshir-audit-" + Path.GetRandomFileName());
        try
        {
            var result = await new PlaybookExecutor(runner, store).RunAsync(pb, "{}", tmp, default);
            result.Kind.Should().Be(PipelineResultKind.Completed);
            runner.LastContext!.ArtifactDir.Should().Be(tmp);
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    // ---- helpers / fakes ----

    private static JsonObject Obj(string json) => (JsonObject)JsonNode.Parse(json)!;

    private static Playbook Pb(params (string Kind, bool ContinueOnFail)[] steps) =>
        new("test", [.. steps.Select((s, i) => new PlaybookStep(i, s.Kind, new JsonObject { ["i"] = i }, s.ContinueOnFail))]);

    private static PipelineRecord RecordFor(Playbook pb, (string Status, bool HashMatches)[] states)
    {
        var steps = pb.Steps.Select((s, i) => new PipelineStepRecord(
            i, s.Kind, states[i].Status,
            states[i].HashMatches ? s.ParamHash : "STALE", null)).ToList();
        return new PipelineRecord(Guid.NewGuid(), pb.Name, "running", steps);
    }

    private sealed class FakeRunner : IStepRunner
    {
        public StepOutcome Default { get; init; } = StepOutcome.Pass("VERDICT: PASS");
        public Dictionary<int, StepOutcome> ByIndex { get; } = new();
        public Exception? Throw { get; init; }
        public int CallCount { get; private set; }
        public PipelineContext? LastContext { get; private set; }

        public Task<StepOutcome> RunAsync(PlaybookStep step, PipelineContext context, CancellationToken ct)
        {
            CallCount++;
            LastContext = context;
            if (Throw is not null) throw Throw;
            return Task.FromResult(ByIndex.TryGetValue(step.Index, out var o) ? o : Default);
        }
    }

    private sealed class FakeStore : IPipelineStore
    {
        private PipelineRecord _record;
        public string PipelineStatus { get; private set; } = "created";
        public Dictionary<int, string> StepStatus { get; } = new();
        public string? LastStepVerdict { get; private set; }

        public FakeStore(PipelineRecord? existing = null)
        {
            _record = existing ?? new PipelineRecord(Guid.NewGuid(), "test", "running", []);
            foreach (var s in _record.Steps) StepStatus[s.StepIndex] = s.Status;
        }

        public Task<PipelineRecord> CreateAsync(Playbook playbook, string playbookJson, string? artifactDir, CancellationToken ct)
        {
            var steps = playbook.Steps.Select(s => new PipelineStepRecord(s.Index, s.Kind, "pending", s.ParamHash, null)).ToList();
            _record = new PipelineRecord(Guid.NewGuid(), playbook.Name, "running", steps);
            foreach (var s in steps) StepStatus[s.StepIndex] = "pending";
            return Task.FromResult(_record);
        }

        public Task<PipelineRecord> GetAsync(Guid id, CancellationToken ct) => Task.FromResult(_record);

        public Task SetPipelineStatusAsync(Guid id, string status, int currentStepIndex, bool completed, CancellationToken ct)
        {
            PipelineStatus = status;
            return Task.CompletedTask;
        }

        public Task SetStepStatusAsync(Guid id, int stepIndex, string status, string? verdictJson, string? artifactPath, string paramHash, CancellationToken ct)
        {
            StepStatus[stepIndex] = status;
            if (status is "passed" or "failed" or "awaiting-owner") LastStepVerdict = verdictJson;
            return Task.CompletedTask;
        }
    }
}

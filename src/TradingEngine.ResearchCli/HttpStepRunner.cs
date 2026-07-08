using System.Text.Json;
using System.Text.Json.Nodes;
using TradingEngine.Domain;

namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.2 (Q3) — the real <see cref="IStepRunner"/>: it runs one playbook step against the running Web app
/// over HTTP and returns a machine <see cref="StepOutcome"/>. It reuses the SAME pure helpers as the
/// single-shot CLI verbs (<see cref="InventoryCoverage"/>, <see cref="StartRunPlan"/>,
/// <see cref="GateEvaluator"/>, <see cref="RunJson"/>) so a step's decision is identical to the
/// equivalent standalone verb — the playbook is just the verbs, stitched and persisted. Control flow
/// (stop/park/continue) is the executor's job; this only does + judges one step.
///
/// The <c>owner-gate</c> step always returns <see cref="StepOutcomeKind.AwaitingOwner"/> the first time
/// it runs; on resume the executor never re-enters an approved gate (it was recorded <c>approved</c> by
/// the API), so the runner is only asked to run a gate that is genuinely pending.
/// </summary>
public sealed class HttpStepRunner : IStepRunner
{
    private readonly ResearchApiClient _client;
    private readonly TimeSpan _pollInterval;

    public HttpStepRunner(ResearchApiClient client, TimeSpan? pollInterval = null)
    {
        _client = client;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public async Task<StepOutcome> RunAsync(PlaybookStep step, PipelineContext context, CancellationToken ct)
    {
        return step.Kind switch
        {
            StepKinds.EnsureData => await EnsureDataAsync(step, ct),
            StepKinds.StartRun => await StartRunAsync(step, context, ct),
            StepKinds.AwaitRun => await AwaitRunAsync(step, context, ct),
            StepKinds.AssertGates => await AssertGatesAsync(step, context, ct),
            StepKinds.Reconcile => await ReconcileAsync(step, context, ct),
            StepKinds.ExitLabEval => await ExitLabAsync(step, ct),
            StepKinds.WalkForward => await WalkForwardAsync(step, ct),
            StepKinds.OwnerGate => OwnerGate(step),
            StepKinds.ApplyCalibration => await ApplyCalibrationAsync(step, ct),
            StepKinds.Report => await Report(step, context),
            _ => StepOutcome.Fail(Verdict.Failing(VerdictField.Of("error", "unknown-step")).Render()),
        };
    }

    private async Task<StepOutcome> EnsureDataAsync(PlaybookStep step, CancellationToken ct)
    {
        var symbols = SplitCsv(Str(step, "symbols"));
        var tfs = SplitCsv(Str(step, "tfs"));
        var from = Date(step, "from");
        var to = Date(step, "to");

        var inventory = InventoryCoverage.ParseInventory(await _client.GetInventoryAsync(ct));
        var cells = InventoryCoverage.Evaluate(inventory, symbols, tfs, from, to);
        var missing = InventoryCoverage.Missing(cells);

        var v = missing.Count == 0
            ? Verdict.Passing(VerdictField.Of("cells", cells.Count), VerdictField.Of("missing", 0))
            : Verdict.Failing(VerdictField.Of("cells", cells.Count), VerdictField.Of("missing", missing.Count),
                VerdictField.Of("gaps", string.Join(",", missing.Select(m => $"{m.Symbol}:{m.Timeframe}"))));
        return missing.Count == 0 ? StepOutcome.Pass(v.Render()) : StepOutcome.Fail(v.Render());
    }

    private async Task<StepOutcome> StartRunAsync(PlaybookStep step, PipelineContext context, CancellationToken ct)
    {
        // A start-run step's params ARE a StartRunRequest, with optional venue/compareBoth/explore overrides.
        var planJson = step.Params.ToJsonString();
        var body = StartRunPlan.BuildBody(planJson, Str(step, "venue"), Bool(step, "compareBoth"), Bool(step, "explore"));
        var (runId, status) = StartRunPlan.ParseStartResponse(await _client.StartRunAsync(body, ct));
        if (string.IsNullOrWhiteSpace(runId))
        {
            return StepOutcome.Fail(Verdict.Failing(VerdictField.Of("error", "no-runId")).Render());
        }
        // Record the runId for downstream await/assert/reconcile steps. A named key lets compare-both
        // record two ids (venue-specific) if a future playbook needs them.
        var key = Str(step, "as") ?? "runId";
        context.Values[key] = runId;
        return StepOutcome.Pass(Verdict.Passing(VerdictField.Of("runId", runId), VerdictField.Of("status", status)).Render());
    }

    private async Task<StepOutcome> AwaitRunAsync(PlaybookStep step, PipelineContext context, CancellationToken ct)
    {
        var runId = ResolveRunId(step, context);
        if (string.IsNullOrWhiteSpace(runId))
        {
            return StepOutcome.Fail(Verdict.Failing(VerdictField.Of("error", "missing-runId")).Render());
        }
        var timeoutSec = Int(step, "timeout", 1800);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        while (!cts.IsCancellationRequested)
        {
            RunGateInput run;
            try
            {
                run = RunJson.ParseRun(await _client.GetRunAsync(runId!, cts.Token));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            if (RunStateMachine.IsTerminal(run.Status))
            {
                return StepOutcome.Pass(Verdict.Passing(
                    VerdictField.Of("status", run.Status),
                    VerdictField.Of("trades", run.TotalTrades)).Render());
            }
            try
            {
                await Task.Delay(_pollInterval, cts.Token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
        return StepOutcome.Fail(Verdict.Failing(VerdictField.Of("error", "await-timeout")).Render());
    }

    private async Task<StepOutcome> AssertGatesAsync(PlaybookStep step, PipelineContext context, CancellationToken ct)
    {
        var runId = ResolveRunId(step, context);
        if (string.IsNullOrWhiteSpace(runId))
        {
            return StepOutcome.Fail(Verdict.Failing(VerdictField.Of("error", "missing-runId")).Render());
        }
        var gates = new GateSpec
        {
            RequireStatus = Str(step, "requireStatus"),
            MinTrades = Int(step, "minTrades", 0),
            ForbidWarnings = Bool(step, "forbidWarnings"),
            ForbidWarningCodes = StrArray(step, "forbidWarningCodes"),
        };
        var run = RunJson.ParseRun(await _client.GetRunAsync(runId!, ct));
        var verdict = GateEvaluator.Evaluate(run, gates);
        return verdict.Pass ? StepOutcome.Pass(verdict.Render()) : StepOutcome.Fail(verdict.Render());
    }

    private async Task<StepOutcome> ReconcileAsync(PlaybookStep step, PipelineContext context, CancellationToken ct)
    {
        var left = ResolveRef(step, context, "left");
        var right = ResolveRef(step, context, "right");
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return StepOutcome.Fail(Verdict.Failing(VerdictField.Of("error", "missing-left-or-right")).Render());
        }
        var json = await _client.GetReconcileAsync(left!, right!, ct);
        var artifact = await WriteArtifactAsync(context, "reconcile.json", json, ct);
        return StepOutcome.Pass(
            Verdict.Passing(VerdictField.Of("left", left!), VerdictField.Of("right", right!)).Render(),
            artifact);
    }

    private async Task<StepOutcome> ExitLabAsync(PlaybookStep step, CancellationToken ct)
    {
        var json = await _client.PostAsync("api/exit-lab/evaluate", step.Params.ToJsonString(), ct);
        var (trades, cells) = ExitLabResult.ParseSummary(json);
        var v = cells > 0
            ? Verdict.Passing(VerdictField.Of("trades", trades), VerdictField.Of("cells", cells))
            : Verdict.Failing(VerdictField.Of("cells", 0), VerdictField.Of("error", "no-cells"));
        return cells > 0 ? StepOutcome.Pass(v.Render()) : StepOutcome.Fail(v.Render());
    }

    private async Task<StepOutcome> WalkForwardAsync(PlaybookStep step, CancellationToken ct)
    {
        var json = await _client.PostAsync("api/walk-forward/start", step.Params.ToJsonString(), ct);
        var jobId = ExitLabResult.ParseJobId(json);
        return string.IsNullOrWhiteSpace(jobId)
            ? StepOutcome.Fail(Verdict.Failing(VerdictField.Of("error", "no-jobId")).Render())
            : StepOutcome.Pass(Verdict.Passing(VerdictField.Of("jobId", jobId)).Render());
    }

    private async Task<StepOutcome> ApplyCalibrationAsync(PlaybookStep step, CancellationToken ct)
    {
        var json = await _client.PostAsync("api/exit-lab/calibrations", step.Params.ToJsonString(), ct);
        var saved = ParseBool(json, "saved");
        if (!saved)
        {
            var error = ParseString(json, "error") ?? "unknown";
            return StepOutcome.Fail(Verdict.Failing(VerdictField.Of("error", error)).Render());
        }
        return StepOutcome.Pass(Verdict.Passing(VerdictField.Of("saved", "true")).Render(), null);
    }

    private static StepOutcome OwnerGate(PlaybookStep step)
    {
        var reason = Str(step, "reason") ?? "owner-approval-required";
        return StepOutcome.AwaitOwner(Verdict.Failing(
            VerdictField.Of("gate", "owner"),
            VerdictField.Of("reason", reason)).Render());
    }

    private async Task<StepOutcome> Report(PlaybookStep step, PipelineContext context)
    {
        // A report step summarizes the context's accumulated values into a markdown file written to the
        // artifact dir. The report itself is always-passing (it's narrative, not a gate).
        var now = DateTime.UtcNow;
        var lines = new List<string>
        {
            $"# Pipeline Report — {context.PipelineId}",
            $"**Generated:** {now:yyyy-MM-dd HH:mm:ss} UTC",
            "",
            "## Accumulated Context",
            "",
            "| Key | Value |",
            "|-----|-------|",
        };
        foreach (var kv in context.Values)
        {
            lines.Add($"| `{EscapeMd(kv.Key)}` | `{EscapeMd(kv.Value)}` |");
        }
        var content = string.Join("\n", lines);
        var artifact = await WriteArtifactAsync(context, "report.md", content, CancellationToken.None);
        var fields = context.Values.Select(kv => VerdictField.Of(kv.Key, kv.Value)).ToArray();
        return StepOutcome.Pass(Verdict.Passing(fields).Render(), artifact);
    }

    private static string EscapeMd(string s) => s.Replace("|", "\\|").Replace("`", "\\`");

    private static async Task<string?> WriteArtifactAsync(PipelineContext context, string fileName, string content, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.ArtifactDir))
        {
            Console.Error.WriteLine($"WARN: pipeline {context.PipelineId}: artifact dir is null — skipping {fileName}");
            return null;
        }
        Directory.CreateDirectory(context.ArtifactDir);
        var path = Path.Combine(context.ArtifactDir, fileName);
        await File.WriteAllTextAsync(path, content, ct);
        return fileName;
    }

    private static string? ResolveRunId(PlaybookStep step, PipelineContext context) => ResolveRef(step, context, "runId");

    // A reference either names a context key (e.g. "runId" set by a start-run step) or is a literal id.
    private static string? ResolveRef(PlaybookStep step, PipelineContext context, string paramName)
    {
        var raw = Str(step, paramName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return context.Values.TryGetValue(paramName, out var v) ? v : null;
        }
        if (raw.StartsWith('$') && context.Values.TryGetValue(raw[1..], out var refv))
        {
            return refv;
        }
        return context.Values.TryGetValue(raw, out var named) ? named : raw;
    }

    private static string? Str(PlaybookStep step, string key) =>
        step.Params.TryGetPropertyValue(key, out var node) && node is not null
            ? node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString()
            : null;

    private static bool Bool(PlaybookStep step, string key) =>
        step.Params.TryGetPropertyValue(key, out var node) && node is not null
        && node.GetValueKind() == JsonValueKind.True;

    private static int Int(PlaybookStep step, string key, int fallback) =>
        step.Params.TryGetPropertyValue(key, out var node) && node is not null && node.GetValueKind() == JsonValueKind.Number
            ? node.GetValue<int>() : fallback;

    private static IReadOnlyList<string> StrArray(PlaybookStep step, string key)
    {
        if (step.Params.TryGetPropertyValue(key, out var node) && node is JsonArray arr)
        {
            return [.. arr.Where(n => n is not null).Select(n => n!.GetValue<string>())];
        }
        return [];
    }

    private static bool ParseBool(string json, string key)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(key, out var el)
            && el.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    private static string? ParseString(string json, string key)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty(key, out var el)
            && el.ValueKind == System.Text.Json.JsonValueKind.String
            ? el.GetString() : null;
    }

    private static List<string> SplitCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? [] : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static DateTime? Date(PlaybookStep step, string key) =>
        DateTime.TryParse(Str(step, key), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
            ? dt : null;
}

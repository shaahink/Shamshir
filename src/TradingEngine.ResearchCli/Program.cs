using TradingEngine.Domain;
using TradingEngine.ResearchCli;

// P3.1 — TradingEngine.ResearchCli entry point. Verbs speak HTTP to the running Web app (Q3) and print a
// single machine `VERDICT:` line last; the process exit code mirrors it (0=PASS, non-zero=FAIL) so a
// playbook step (P3.2) branches without parsing. No interactive prompts, ever. This session lands the
// foundation verbs (run await, run validate, reconcile); the remaining verbs + the playbook engine
// (P3.2) + the UI review page (P3.3) are the next checkpoints.

var cli = CliArgs.Parse(args);
var baseUrl = cli.Option("base-url", Environment.GetEnvironmentVariable("SHAMSHIR_BASE_URL") ?? "https://localhost:7108");
var timeout = TimeSpan.FromSeconds(cli.Option("timeout", 1800));

try
{
    switch (cli.Verb)
    {
        case "data ensure":
            return await DataEnsureAsync(cli, baseUrl, timeout);
        case "data quality":
            return await DataQualityAsync(cli, baseUrl, timeout);
        case "run start":
            return await RunStartAsync(cli, baseUrl, timeout);
        case "run validate":
            return await RunValidateAsync(cli, baseUrl, timeout);
        case "run await":
            return await RunAwaitAsync(cli, baseUrl, timeout);
        case "reconcile":
            return await ReconcileAsync(cli, baseUrl, timeout);
        case "exitlab eval":
            return await ExitLabEvalAsync(cli, baseUrl, timeout);
        case "walkforward":
            return await WalkForwardAsync(cli, baseUrl, timeout);
        case "pipeline run":
            return await PipelineRunAsync(cli, baseUrl, timeout);
        case "pipeline status":
            return await PipelineStatusAsync(cli, baseUrl, timeout);
        case "pipeline approve":
            return await PipelineApproveAsync(cli, baseUrl, timeout, approve: true);
        case "pipeline reject":
            return await PipelineApproveAsync(cli, baseUrl, timeout, approve: false);
        case "entry-quality":
            return await EntryQualityAsync(cli, baseUrl, timeout);
        case "pyramid-eval":
            return await PyramidEvalAsync(cli, baseUrl, timeout);
        default:
            PrintUsage();
            Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "unknown-verb")).Render());
            return 2;
    }
}
catch (Exception ex)
{
    // Diagnostics bundle: the agent never needs to open the UI (PLAN §6 P3.1). Verdict is last.
    Console.Error.WriteLine($"DIAG: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(Verdict.Failing(VerdictField.Of("error", ex.GetType().Name)).Render());
    return 3;
}

static async Task<int> RunValidateAsync(CliArgs cli, string baseUrl, TimeSpan timeout)
{
    var runId = cli.Positionals.ElementAtOrDefault(2);
    if (string.IsNullOrWhiteSpace(runId))
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "missing-runId")).Render());
        return 2;
    }

    var gates = new GateSpec
    {
        RequireStatus = cli.Option("require-status"),
        MinTrades = cli.Option("min-trades", 0),
        ForbidWarnings = cli.Flag("forbid-warnings"),
        ForbidWarningCodes = cli.Option("forbid-warning-code") is { } code and not "" ? [code] : [],
    };

    using var client = new ResearchApiClient(baseUrl, timeout);
    var json = await client.GetRunAsync(runId, CancellationToken.None);
    var run = RunJson.ParseRun(json);
    var verdict = GateEvaluator.Evaluate(run, gates);

    if (cli.Flag("json"))
    {
        Console.WriteLine(json);
    }
    Console.WriteLine(verdict.Render());
    return verdict.ExitCode;
}

static async Task<int> RunAwaitAsync(CliArgs cli, string baseUrl, TimeSpan timeout)
{
    var runId = cli.Positionals.ElementAtOrDefault(2);
    if (string.IsNullOrWhiteSpace(runId))
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "missing-runId")).Render());
        return 2;
    }

    using var client = new ResearchApiClient(baseUrl, timeout);
    using var cts = new CancellationTokenSource(timeout);
    var pollMs = cli.Option("poll-ms", 2000);

    while (!cts.IsCancellationRequested)
    {
        string json;
        try
        {
            json = await client.GetRunAsync(runId, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // The overall await budget elapsed while a poll request was in flight — fall through to the
            // deterministic `await-timeout` verdict below rather than leaking a generic exception verdict
            // (an agent branches on VERDICT=FAIL error=await-timeout, not on error=TaskCanceledException).
            break;
        }
        var run = RunJson.ParseRun(json);
        if (RunStateMachine.IsTerminal(run.Status))
        {
            Console.WriteLine(Verdict.Passing(
                VerdictField.Of("status", run.Status),
                VerdictField.Of("trades", run.TotalTrades)).Render());
            return 0;
        }
        Console.Error.WriteLine($"… {runId} status={run.Status} trades={run.TotalTrades}");
        try
        {
            await Task.Delay(pollMs, cts.Token);
        }
        catch (TaskCanceledException)
        {
            break;
        }
    }

    Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "await-timeout")).Render());
    return 1;
}

static async Task<int> ReconcileAsync(CliArgs cli, string baseUrl, TimeSpan timeout)
{
    var left = cli.Option("left");
    var right = cli.Option("right");
    if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "missing-left-or-right")).Render());
        return 2;
    }

    using var client = new ResearchApiClient(baseUrl, timeout);
    var json = await client.GetReconcileAsync(left, right, CancellationToken.None);
    if (cli.Flag("json"))
    {
        Console.WriteLine(json);
    }
    Console.WriteLine(Verdict.Passing(
        VerdictField.Of("left", left),
        VerdictField.Of("right", right)).Render());
    return 0;
}

static async Task<int> DataEnsureAsync(CliArgs cli, string baseUrl, TimeSpan timeout)
{
    var symbols = SplitCsv(cli.Option("symbols"));
    var tfs = SplitCsv(cli.Option("tfs"));
    if (symbols.Count == 0 || tfs.Count == 0)
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "missing-symbols-or-tfs")).Render());
        return 2;
    }
    var from = ParseDate(cli.Option("from"));
    var to = ParseDate(cli.Option("to"));

    using var client = new ResearchApiClient(baseUrl, timeout);
    var inventoryJson = await client.GetInventoryAsync(CancellationToken.None);
    var inventory = InventoryCoverage.ParseInventory(inventoryJson);
    var cells = InventoryCoverage.Evaluate(inventory, symbols, tfs, from, to);
    var missing = InventoryCoverage.Missing(cells);

    if (missing.Count == 0)
    {
        Console.WriteLine(Verdict.Passing(
            VerdictField.Of("cells", cells.Count),
            VerdictField.Of("missing", 0)).Render());
        return 0;
    }

    // Per Q3 the CLI drives the running app; it kicks the download but never blocks the pipeline on a
    // long ingest here — the playbook's `ensure-data` step (P3.2) re-checks coverage as its own gate.
    if (cli.Flag("download"))
    {
        foreach (var group in missing.GroupBy(m => m.Symbol, StringComparer.OrdinalIgnoreCase))
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                symbol = group.Key,
                tfs = group.Select(g => g.Timeframe).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                from,
                to,
                days = from is null ? 30 : 0,
            });
            await client.StartDownloadAsync(body, CancellationToken.None);
        }
    }

    var detail = string.Join(",", missing.Select(m => $"{m.Symbol}:{m.Timeframe}"));
    Console.WriteLine(Verdict.Failing(
        VerdictField.Of("cells", cells.Count),
        VerdictField.Of("missing", missing.Count),
        VerdictField.Of("downloadQueued", cli.Flag("download") ? "true" : "false"),
        VerdictField.Of("gaps", detail)).Render());
    return 1;
}

static async Task<int> DataQualityAsync(CliArgs cli, string baseUrl, TimeSpan timeout)
{
    using var client = new ResearchApiClient(baseUrl, timeout);
    var json = await client.GetAsync("api/data-manager/quality-report", CancellationToken.None);

    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var root = doc.RootElement;

    var bars = root.TryGetProperty("totalBars", out var tb) ? tb.GetInt64() : 0;
    var violations = root.TryGetProperty("totalViolations", out var tv) ? tv.GetInt32() : -1;
    var ohlcCount = root.TryGetProperty("ohlcViolations", out var ov) ? ov.GetArrayLength() : 0;
    var gapCount = root.TryGetProperty("gapEntries", out var ge) ? ge.GetArrayLength() : 0;

    var fields = new List<VerdictField>
    {
        VerdictField.Of("totalBars", bars.ToString()),
        VerdictField.Of("violations", violations.ToString()),
        VerdictField.Of("ohlcViolations", ohlcCount.ToString()),
        VerdictField.Of("gaps", gapCount.ToString()),
    };

    if (violations == 0)
    {
        Console.WriteLine(Verdict.Passing([.. fields]).Render());
        return 0;
    }

    Console.WriteLine(Verdict.Failing([.. fields]).Render());
    return 1;
}

static async Task<int> RunStartAsync(CliArgs cli, string baseUrl, TimeSpan timeout)
{
    var planPath = cli.Option("plan");
    if (string.IsNullOrWhiteSpace(planPath) || !File.Exists(planPath))
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "missing-plan")).Render());
        return 2;
    }

    var planJson = await File.ReadAllTextAsync(planPath);
    string body;
    try
    {
        body = StartRunPlan.BuildBody(planJson, cli.Option("venue"), cli.Flag("compare-both"), cli.Flag("explore"));
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"DIAG: {ex.Message}");
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "invalid-plan")).Render());
        return 2;
    }

    using var client = new ResearchApiClient(baseUrl, timeout);
    var respJson = await client.StartRunAsync(body, CancellationToken.None);
    var (runId, status) = StartRunPlan.ParseStartResponse(respJson);
    if (string.IsNullOrWhiteSpace(runId))
    {
        Console.WriteLine(respJson);
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "no-runId")).Render());
        return 1;
    }

    Console.WriteLine(Verdict.Passing(
        VerdictField.Of("runId", runId),
        VerdictField.Of("status", status)).Render());
    return 0;
}

static async Task<int> ExitLabEvalAsync(CliArgs cli, string baseUrl, TimeSpan timeout)
{
    var gridPath = cli.Option("grid");
    if (string.IsNullOrWhiteSpace(gridPath) || !File.Exists(gridPath))
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "missing-grid")).Render());
        return 2;
    }

    var body = await File.ReadAllTextAsync(gridPath);
    using var client = new ResearchApiClient(baseUrl, timeout);
    var json = await client.PostAsync("api/exit-lab/evaluate", body, CancellationToken.None);
    if (cli.Flag("json"))
    {
        Console.WriteLine(json);
    }

    var (totalTrades, totalCells) = ExitLabResult.ParseSummary(json);
    var verdict = totalCells > 0
        ? Verdict.Passing(VerdictField.Of("trades", totalTrades), VerdictField.Of("cells", totalCells))
        : Verdict.Failing(VerdictField.Of("trades", totalTrades), VerdictField.Of("cells", 0), VerdictField.Of("error", "no-cells"));
    Console.WriteLine(verdict.Render());
    return verdict.ExitCode;
}

static async Task<int> WalkForwardAsync(CliArgs cli, string baseUrl, TimeSpan timeout)
{
    var specPath = cli.Option("spec");
    if (string.IsNullOrWhiteSpace(specPath) || !File.Exists(specPath))
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "missing-spec")).Render());
        return 2;
    }

    var body = await File.ReadAllTextAsync(specPath);
    using var client = new ResearchApiClient(baseUrl, timeout);
    var json = await client.PostAsync("api/walk-forward/start", body, CancellationToken.None);
    var jobId = ExitLabResult.ParseJobId(json);
    if (string.IsNullOrWhiteSpace(jobId))
    {
        Console.WriteLine(json);
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "no-jobId")).Render());
        return 1;
    }
    Console.WriteLine(Verdict.Passing(VerdictField.Of("jobId", jobId)).Render());
    return 0;
}

static async Task<int> PipelineRunAsync(CliArgs cli, string baseUrl, TimeSpan timeout)
{
    var path = cli.Positionals.ElementAtOrDefault(2) ?? cli.Option("playbook");
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "missing-playbook")).Render());
        return 2;
    }

    var playbookJson = await File.ReadAllTextAsync(path);
    Playbook playbook;
    try
    {
        playbook = PlaybookParser.Parse(playbookJson);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"DIAG: {ex.Message}");
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "invalid-playbook")).Render());
        return 2;
    }

    using var client = new ResearchApiClient(baseUrl, timeout);
    var runner = new HttpStepRunner(client, TimeSpan.FromMilliseconds(cli.Option("poll-ms", 2000)));
    var store = new ApiPipelineStore(client);
    var executor = new PlaybookExecutor(runner, store);

    PipelineResult result;
    if (Guid.TryParse(cli.Option("resume"), out var resumeId))
    {
        result = await executor.ResumeAsync(playbook, resumeId, ResolveArtifactDir(cli, resumeId), CancellationToken.None);
    }
    else
    {
        // Artifact dir is created lazily once we know the id — start with the base and let the CLI thread
        // the pipeline id in via a second (resume) invocation if the owner wants a stable path up front.
        var pipelineDir = cli.Option("artifact-dir");
        result = await executor.RunAsync(playbook, playbookJson, pipelineDir, CancellationToken.None);
    }

    Console.WriteLine(result.ToVerdict().Render());
    return result.ToVerdict().ExitCode;
}

static string? ResolveArtifactDir(CliArgs cli, Guid id)
{
    var baseDir = cli.Option("artifact-dir");
    return string.IsNullOrWhiteSpace(baseDir) ? null : baseDir;
}

static async Task<int> PipelineStatusAsync(CliArgs cli, string baseUrl, TimeSpan timeout)
{
    var id = cli.Positionals.ElementAtOrDefault(2) ?? cli.Option("id");
    if (string.IsNullOrWhiteSpace(id))
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "missing-id")).Render());
        return 2;
    }

    using var client = new ResearchApiClient(baseUrl, timeout);
    var json = await client.GetAsync($"api/research/pipelines/{id}", CancellationToken.None);
    if (cli.Flag("json"))
    {
        Console.WriteLine(json);
    }
    var record = ApiPipelineStore.ParseRecord(json);
    var passed = record.Steps.Count(s => s.Status is "passed" or "approved" or "skipped");
    Console.WriteLine(Verdict.Passing(
        VerdictField.Of("pipeline", record.Id.ToString()),
        VerdictField.Of("status", record.Status),
        VerdictField.Of("passed", passed),
        VerdictField.Of("steps", record.Steps.Count)).Render());
    return 0;
}

static async Task<int> PipelineApproveAsync(CliArgs cli, string baseUrl, TimeSpan timeout, bool approve)
{
    var id = cli.Positionals.ElementAtOrDefault(2) ?? cli.Option("id");
    if (string.IsNullOrWhiteSpace(id))
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "missing-id")).Render());
        return 2;
    }

    using var client = new ResearchApiClient(baseUrl, timeout);
    var verb = approve ? "approve" : "reject";
    await client.PostAsync($"api/research/pipelines/{id}/{verb}", "{}", CancellationToken.None);
    Console.WriteLine(Verdict.Passing(
        VerdictField.Of("pipeline", id),
        VerdictField.Of("decision", verb)).Render());
    return 0;
}

static async Task<int> EntryQualityAsync(CliArgs cli, string baseUrl, TimeSpan timeout)
{
    var runId = cli.Positionals.ElementAtOrDefault(2);
    if (string.IsNullOrWhiteSpace(runId))
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "missing-runId")).Render());
        return 2;
    }

    var strategyId = cli.Option("strategy");
    var minTrades = cli.Option("min-trades", 10);

    var query = $"api/entry-quality?runId={Uri.EscapeDataString(runId)}&minTrades={minTrades}";
    if (!string.IsNullOrEmpty(strategyId))
        query += $"&strategyId={Uri.EscapeDataString(strategyId)}";

    using var client = new ResearchApiClient(baseUrl, timeout);
    var json = await client.GetAsync(query, CancellationToken.None);

    if (cli.Flag("json"))
        Console.WriteLine(json);

    if (json is null)
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "no-response")).Render());
        return 1;
    }

    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var root = doc.RootElement;

    if (root.TryGetProperty("error", out var err))
    {
        Console.WriteLine(Verdict.Failing(
            VerdictField.Of("error", err.GetString() ?? "unknown"),
            VerdictField.Of("totalTrades", root.TryGetProperty("totalTrades", out var tt) ? tt.GetInt32() : 0)).Render());
        return 1;
    }

    var rSquared = root.TryGetProperty("rSquared", out var rs) ? rs.GetDouble() : 0.0;
    var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
    var observations = root.TryGetProperty("validObservations", out var vo) ? vo.GetInt32() : 0;

    var fields = new List<VerdictField>
    {
        VerdictField.Of("runId", runId),
        VerdictField.Of("observations", observations),
        VerdictField.Of("rSquared", Math.Round(rSquared, 4).ToString()),
        VerdictField.Of("parameters", (root.TryGetProperty("parameters", out var p) ? p.GetInt32() : 0).ToString()),
    };

    if (root.TryGetProperty("features", out var feats) && feats.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        foreach (var f in feats.EnumerateArray())
        {
            var name = f.GetProperty("name").GetString() ?? "?";
            var coeff = f.GetProperty("coefficient").GetDouble();
            var tStat = f.GetProperty("tStatistic").GetDouble();
            if (Math.Abs(tStat) > 1.5)
            {
                var dir = coeff > 0 ? "+" : "";
                fields.Add(VerdictField.Of(name, $"{dir}{coeff:F4} t={tStat:F1}"));
            }
        }
    }

    Console.WriteLine(Verdict.Passing(fields.ToArray()).Render());
    return 0;
}

static async Task<int> PyramidEvalAsync(CliArgs cli, string baseUrl, TimeSpan timeout)
{
    var runId = cli.Positionals.ElementAtOrDefault(2);
    if (string.IsNullOrWhiteSpace(runId))
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "missing-runId")).Render());
        return 2;
    }

    var strategyId = cli.Option("strategy");
    var minTrades = cli.Option("min-trades", 10);
    var addLevelsStr = cli.Option("add-levels");

    var body = new System.Text.Json.Nodes.JsonObject
    {
        ["runId"] = runId,
        ["minTrades"] = minTrades,
    };
    if (!string.IsNullOrEmpty(strategyId))
        body["strategyId"] = strategyId;
    if (!string.IsNullOrEmpty(addLevelsStr))
    {
        var arr = new System.Text.Json.Nodes.JsonArray();
        foreach (var s in addLevelsStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                arr.Add(v);
        }
        if (arr.Count > 0) body["addLevels"] = arr;
    }

    using var client = new ResearchApiClient(baseUrl, timeout);
    var json = await client.PostAsync("api/exit-lab/pyramid-eval", body.ToJsonString(), CancellationToken.None);

    if (cli.Flag("json"))
        Console.WriteLine(json);

    if (json is null)
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", "no-response")).Render());
        return 1;
    }

    using var doc = System.Text.Json.JsonDocument.Parse(json);
    var root = doc.RootElement;

    if (root.TryGetProperty("error", out var err))
    {
        Console.WriteLine(Verdict.Failing(VerdictField.Of("error", err.GetString() ?? "unknown")).Render());
        return 1;
    }

    var totalTrades = root.TryGetProperty("totalTrades", out var tt) ? tt.GetInt32() : 0;
    var levels = root.TryGetProperty("levels", out var lvls) ? lvls : default;
    var levelCount = levels.ValueKind == System.Text.Json.JsonValueKind.Array ? levels.GetArrayLength() : 0;

    var fields = new List<VerdictField>
    {
        VerdictField.Of("runId", runId),
        VerdictField.Of("totalTrades", totalTrades),
        VerdictField.Of("levels", levelCount),
    };

    if (levels.ValueKind == System.Text.Json.JsonValueKind.Array)
    {
        foreach (var level in levels.EnumerateArray())
        {
            var addAtR = level.GetProperty("addAtR").GetDouble();
            var avgImprovement = level.GetProperty("avgImprovement").GetDouble();
            var triggerRate = level.GetProperty("triggerRate").GetDouble();
            if (triggerRate > 0.1)
            {
                var dir = avgImprovement > 0 ? "+" : "";
                fields.Add(VerdictField.Of($"addAt{addAtR}R", $"{dir}{avgImprovement:F3} improvement, {triggerRate:P0} trigger"));
            }
        }
    }

    Console.WriteLine(Verdict.Passing(fields.ToArray()).Render());
    return totalTrades > 0 ? 0 : 1;
}

static List<string> SplitCsv(string? csv) =>
    string.IsNullOrWhiteSpace(csv)
        ? []
        : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

static DateTime? ParseDate(string? value) =>
    DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
        ? dt : null;

static void PrintUsage()
{
        Console.Error.WriteLine("""
        TradingEngine.ResearchCli (research) — drives the running Shamshir Web app over HTTP.
        Usage:
          research data ensure  --symbols EURUSD,XAUUSD --tfs H1,M15 [--from 2026-01-01 --to 2026-07-01] [--download]
          research data quality [--base-url https://localhost:7108]
          research run start    --plan plan.json [--venue tape] [--compare-both] [--explore]
          research run validate <runId> [--require-status completed] [--min-trades 1]
                                        [--forbid-warnings] [--forbid-warning-code TRADES_LOST] [--json]
          research run await    <runId> [--timeout 1800] [--poll-ms 2000]
          research reconcile    --left <runId> --right <runId> [--json]
          research exitlab eval --grid grid.json [--json]
          research walkforward  --spec spec.json
          research pipeline run    <playbook.json> [--resume <id>] [--artifact-dir <dir>] [--poll-ms 2000]
          research pipeline status <id> [--json]
          research pipeline approve <id>
          research pipeline reject  <id>
          research entry-quality <runId> [--strategy <id>] [--min-trades 10] [--json]
          research pyramid-eval  <runId> [--strategy <id>] [--min-trades 10] [--add-levels 0.5,1.0,1.5] [--json]
        Global: [--base-url https://localhost:7108] (or env SHAMSHIR_BASE_URL)
        Every command prints a final `VERDICT: PASS|FAIL …` line; exit code mirrors it.
        """);
}

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
        case "run validate":
            return await RunValidateAsync(cli, baseUrl, timeout);
        case "run await":
            return await RunAwaitAsync(cli, baseUrl, timeout);
        case "reconcile":
            return await ReconcileAsync(cli, baseUrl, timeout);
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

static void PrintUsage()
{
    Console.Error.WriteLine("""
        TradingEngine.ResearchCli (research) — drives the running Shamshir Web app over HTTP.
        Usage:
          research run validate <runId> [--require-status completed] [--min-trades 1]
                                        [--forbid-warnings] [--forbid-warning-code TRADES_LOST] [--json]
          research run await    <runId> [--timeout 1800] [--poll-ms 2000]
          research reconcile    --left <runId> --right <runId> [--json]
        Global: [--base-url https://localhost:7108] (or env SHAMSHIR_BASE_URL)
        Every command prints a final `VERDICT: PASS|FAIL …` line; exit code mirrors it.
        """);
}

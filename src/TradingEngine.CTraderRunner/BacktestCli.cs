using System.Diagnostics;
using System.Text;

namespace TradingEngine.CTraderRunner;

public static class BacktestCli
{
    public static async Task<BacktestCliResult> InvokeAsync(
        BacktestCliRequest request, CancellationToken ct = default, Action<int>? onStarted = null)
    {
        var effectiveCt = ct;
        var timeoutCts = (CancellationTokenSource?)null;
        if (request.TimeoutSeconds > 0)
        {
            timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
            effectiveCt = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token).Token;
        }

        try
        {
            return await InvokeCoreAsync(request, effectiveCt, onStarted);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            return new BacktestCliResult
            {
                ExitCode = -1,
                StdErr = $"cTrader CLI timed out after {request.TimeoutSeconds}s",
                IsKnownCrash = false,
            };
        }
    }

    private static async Task<BacktestCliResult> InvokeCoreAsync(
        BacktestCliRequest request, CancellationToken ct, Action<int>? onStarted = null)
    {
        ChildProcessReaper.EnsureCurrentProcessInKillOnCloseJob();

        var cliPath = CTraderCliLocator.Locate(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());

        Console.WriteLine($"[BacktestCli] Path={cliPath}");

        var argString = BuildArgs(request);

        var procStartUtc = DateTime.UtcNow;
        var swProc = System.Diagnostics.Stopwatch.StartNew();
        using var process = Process.Start(new ProcessStartInfo(cliPath, argString)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"Failed to start ctrader-cli at {cliPath}");

        // X4: hand the PID to the owner so it can tree-kill only what it launched (never by image name).
        try { onStarted?.Invoke(process.Id); } catch { /* registration is best-effort */ }

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            swProc.Stop();

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (request.Diagnostics)
            {
                try
                {
                    var dir = Path.Combine(Path.GetTempPath(), "shamshir-profiling");
                    Directory.CreateDirectory(dir);
                    var firstStamp = stdout.Split('\n')
                        .Select(l => l.Trim())
                        .FirstOrDefault(l => l.Length > 23 && l.Contains(" | ") && DateTime.TryParse(l[..23], out _));
                    File.AppendAllText(Path.Combine(dir, "ctrader-cli-timing.log"),
                        $"procStartUtc={procStartUtc:O}\tprocWallMs={swProc.ElapsedMilliseconds}\tfirstCliLog={firstStamp?[..Math.Min(23, firstStamp.Length)]}\trange={request.Start:yyyy-MM-dd}..{request.End:yyyy-MM-dd}\n");
                }
                catch { /* measurement only */ }
            }

            var isKnownCrash = process.ExitCode != 0
                && (stderr.Contains("Message expected") || stderr.Contains("Object reference")
                    || stdout.Contains("Message expected") || stdout.Contains("Object reference"));

            var cbotLines = stdout.Split('\n')
                .Where(l => l.Contains("CBOT|"))
                .Select(l => l.Trim())
                .ToList();

            return new BacktestCliResult
            {
                ExitCode = process.ExitCode,
                StdOut = stdout,
                StdErr = stderr,
                IsKnownCrash = isKnownCrash,
                ReportJsonPath = request.ReportJsonPath,
                CbotLines = cbotLines,
                CliPath = cliPath,
            };
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            throw;
        }
    }

    internal static string BuildArgs(BacktestCliRequest r)
    {
        var sb = new StringBuilder();
        sb.Append($"backtest \"{r.AlgoPath}\"");
        sb.Append($" --start={r.Start:dd/MM/yyyy}");
        sb.Append($" --end={r.End:dd/MM/yyyy}");
        sb.Append($" --symbol={r.Symbol}");
        sb.Append($" --period={r.Period.ToLowerInvariant()}");
        sb.Append($" --balance={r.Balance}");
        sb.Append($" --commission={r.CommissionPerMillion}");
        sb.Append($" --spread={r.SpreadPips}");
        sb.Append($" --data-mode={r.DataMode}");
        sb.Append($" --ctid={r.CtId}");
        sb.Append($" --pwd-file=\"{r.PwdFile}\"");
        sb.Append($" --account={r.Account}");
        sb.Append($" --DataPort={r.DataPort}");
        sb.Append($" --CommandPort={r.CommandPort}");

        if (r.Symbols.Count > 0)
            sb.Append($" --SymbolString={string.Join(",", r.Symbols)}");
        if (r.Periods.Count > 0)
            sb.Append($" --Periods={string.Join(",", r.Periods)}");
        if (r.FullAccess)
            sb.Append(" --full-access");
        if (r.Diagnostics)
            sb.Append(" --Diagnostics=true");
        // cBot parameter: where the cBot writes its OWN report.json + events.json (ShamshirTradeLogger).
        if (!string.IsNullOrWhiteSpace(r.ReportDir))
            sb.Append($" --ReportPath=\"{r.ReportDir}\"");
        // NOTE: do NOT pass --report-json. The flag crashes cTrader-cli's
        // BacktestReportSavingStateStrategy ("Message expected" → NotImplementedException),
        // which ALSO suppresses the report.html + events.json that cTrader writes to its
        // Backtesting dir by default. We harvest those instead (report.html embeds the full
        // summary JSON; events.json carries the per-event ledger). r.ReportJsonPath is now the
        // artifact path we COPY the harvested summary to, not a CLI flag.
        if (r.DataDir is not null)
            sb.Append($" --data-dir=\"{r.DataDir}\"");
        if (r.DataFile is not null)
            sb.Append($" --data-file=\"{r.DataFile}\"");
        if (r.Record)
            sb.Append(" --Record=true");

        return sb.ToString();
    }
}

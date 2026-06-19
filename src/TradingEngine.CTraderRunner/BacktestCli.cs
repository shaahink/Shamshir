using System.Diagnostics;
using System.Text;

namespace TradingEngine.CTraderRunner;

public static class BacktestCli
{
    public static async Task<BacktestCliResult> InvokeAsync(
        BacktestCliRequest request, CancellationToken ct = default)
    {
        ChildProcessReaper.EnsureCurrentProcessInKillOnCloseJob();

        var cliPath = CTraderCliLocator.Locate(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());

        Console.WriteLine($"[BacktestCli] Path={cliPath}");

        var argString = BuildArgs(request);

        using var process = Process.Start(new ProcessStartInfo(cliPath, argString)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException($"Failed to start ctrader-cli at {cliPath}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

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

        return sb.ToString();
    }
}

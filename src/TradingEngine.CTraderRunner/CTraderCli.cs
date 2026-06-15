using System.Text;
using CliWrap;
using CliWrap.EventStream;
using Microsoft.Extensions.Configuration;

namespace TradingEngine.CTraderRunner;

public sealed class CTraderCli
{
    private readonly string _cliPath;

    public CTraderCli(string? cliPath = null)
    {
        _cliPath = cliPath ?? CTraderCliLocator.Locate(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());
    }

    public static string LocateCli(IConfiguration? config = null)
        => CTraderCliLocator.Locate(config
            ?? new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build());

    public async Task<CTraderResult> BacktestAsync(
        string algoPath,
        string[] extraArgs,
        CancellationToken ct = default)
    {
        // Arm the kill-on-close reaper before spawning, so ctrader-cli and any of its own
        // children die with this process even if the run is cancelled or the host crashes.
        ChildProcessReaper.EnsureCurrentProcessInKillOnCloseJob();

        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();

        var args = new[] { "backtest", algoPath }.Concat(extraArgs);

        var result = await Cli.Wrap(_cliPath)
            .WithArguments(args)
            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(stdOut))
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErr))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);

        var stdout = stdOut.ToString();
        var stderr = stdErr.ToString();

        return new CTraderResult(
            result.ExitCode,
            stdout,
            stderr,
            stdout.Contains("Message expected") || stdout.Contains("Object reference"));
    }
}

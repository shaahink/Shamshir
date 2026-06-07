using System.Diagnostics;

namespace TradingEngine.CTraderRunner;

public sealed class BacktestRunner
{
    private readonly IConfiguration _config;
    private readonly ILogger<BacktestRunner> _logger;

    public BacktestRunner(IConfiguration config, ILogger<BacktestRunner> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<BacktestResult> RunAsync(BacktestConfig cfg, CancellationToken ct = default)
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        var pipeName = "trading-engine";
        var resultsDir = Path.Combine(Path.GetTempPath(), "shamshir-backtest", runId);
        Directory.CreateDirectory(resultsDir);
        var reportJsonPath = Path.Combine(resultsDir, "report.json");

        // 1. Start engine subprocess
        var engineProcess = StartEngine(pipeName, runId);
        _logger.LogInformation("Engine started. PID={Pid} Pipe={PipeName} RunId={RunId}",
            engineProcess?.Id ?? -1, pipeName, runId);

        try
        {
            // 2. Wait for pipe to be ready
            await WaitForPipeAsync(pipeName, TimeSpan.FromSeconds(10), ct);

            // 3. Start ctrader-cli
            var cliPath = CTraderCliLocator.Locate(_config);
            var algoPath = ResolveAlgoPath();
            var args = BuildArgs(cfg, algoPath, pipeName, reportJsonPath);

            _logger.LogInformation("Starting ctrader-cli backtest. RunId={RunId}", runId);
            using var cliProcess = Process.Start(new ProcessStartInfo(cliPath, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("Failed to start ctrader-cli");

            var stdoutTask = cliProcess.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = cliProcess.StandardError.ReadToEndAsync(ct);
            await cliProcess.WaitForExitAsync(ct);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (!string.IsNullOrWhiteSpace(stderr))
                _logger.LogWarning("ctrader-cli stderr: {Stderr}", stderr);

            return new BacktestResult
            {
                RunId = runId,
                ExitCode = cliProcess.ExitCode,
                ErrorMessage = cliProcess.ExitCode != 0 ? stderr : null,
            };
        }
        finally
        {
            // 4. Kill engine subprocess
            if (engineProcess is not null && !engineProcess.HasExited)
            {
                engineProcess.Kill(entireProcessTree: true);
                await engineProcess.WaitForExitAsync(CancellationToken.None);
                _logger.LogInformation("Engine process killed. PID={Pid}", engineProcess.Id);
            }
        }
    }

    private Process? StartEngine(string pipeName, string runId)
    {
        var baseDir = AppContext.BaseDirectory;
        var engineProjPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
            "src", "TradingEngine.Host", "TradingEngine.Host.csproj"));

        if (!File.Exists(engineProjPath))
        {
            _logger.LogWarning("Engine project not found at {Path} — starting engine subprocess skipped", engineProjPath);
            return null;
        }

        var psi = new ProcessStartInfo("dotnet", $"run --project \"{engineProjPath}\" --no-build -- --Engine:Mode Live --Engine:Broker:PipeName {pipeName}")
        {
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment =
            {
                ["Engine__Mode"] = "Live",
                ["Engine__Broker__PipeName"] = pipeName,
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
            },
        };

        var process = Process.Start(psi);
        _logger.LogInformation("Engine subprocess started. PID={Pid} Project={Project}", process?.Id, engineProjPath);
        return process;
    }

    private static async Task WaitForPipeAsync(string pipeName, TimeSpan timeout, CancellationToken ct)
    {
        var pipePath = $@"\\.\pipe\{pipeName}";
        var deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(pipePath))
                return;
            await Task.Delay(300, ct);
        }

        throw new TimeoutException($"Engine did not become ready within {timeout.TotalSeconds}s — pipe '{pipeName}' not found");
    }

    private string BuildArgs(BacktestConfig cfg, string algoPath, string pipeName, string reportJsonPath)
    {
        var sb = new StringBuilder();
        sb.Append($"backtest \"{algoPath}\"");
        sb.Append($" --start={cfg.Start:dd/MM/yyyy}");
        sb.Append($" --end={cfg.End:dd/MM/yyyy}");
        sb.Append($" --symbol={cfg.Symbol}");
        sb.Append($" --period={cfg.Period}");
        sb.Append($" --balance={cfg.Balance}");
        sb.Append($" --commission={cfg.CommissionPerMillion}");
        sb.Append($" --spread={cfg.SpreadPips}");
        sb.Append($" --data-mode={cfg.DataMode}");
        if (cfg.DataDir is not null) sb.Append($" --data-dir=\"{cfg.DataDir}\"");
        if (cfg.DataFile is not null) sb.Append($" --data-file=\"{cfg.DataFile}\"");
        sb.Append($" --report-json=\"{reportJsonPath}\"");
        sb.Append($" --ctid={_config["CTrader:CtId"]}");
        sb.Append($" --pwd-file=\"{_config["CTrader:PwdFile"]}\"");
        sb.Append($" --account={_config["CTrader:Account"]}");
        sb.Append($" --PipeName={pipeName}");
        sb.Append(" --exit-on-stop");
        foreach (var (key, value) in cfg.CustomParams)
            sb.Append($" --{key}={value}");
        return sb.ToString();
    }

    private string ResolveAlgoPath()
    {
        var configured = _config["CTrader:AlgoPath"];
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured))
            return configured;

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Release", "net6.0", "src.algo")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
                "src", "TradingEngine.Adapters.CTrader", "bin", "Debug", "net6.0", "src.algo")),
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("src.algo not found. Build TradingEngine.Adapters.CTrader first.");
    }
}

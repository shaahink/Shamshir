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
        var cliPath = CTraderCliLocator.Locate(_config);
        var algoPath = ResolveAlgoPath();
        var runId = Guid.NewGuid().ToString("N")[..8];
        var pipeName = "trading-engine";
        var resultsDir = Path.Combine(Path.GetTempPath(), "shamshir-backtest", runId);
        Directory.CreateDirectory(resultsDir);
        var reportJsonPath = Path.Combine(resultsDir, "report.json");

        var args = BuildArgs(cfg, algoPath, pipeName, reportJsonPath);
        _logger.LogInformation("Starting ctrader-cli backtest. RunId={RunId}", runId);

        var psi = new ProcessStartInfo(cliPath, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ctrader-cli");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (!string.IsNullOrWhiteSpace(stderr))
            _logger.LogWarning("ctrader-cli stderr: {Stderr}", stderr);

        return new BacktestResult
        {
            RunId = runId,
            ExitCode = process.ExitCode,
            ErrorMessage = process.ExitCode != 0 ? stderr : null,
        };
    }

    private static string BuildArgs(BacktestConfig cfg, string algoPath, string pipeName, string reportJsonPath)
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

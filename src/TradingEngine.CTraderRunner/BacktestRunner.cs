using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

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
        var dataPort = int.TryParse(_config["Engine:Broker:NetMQ:DataPort"], out var dp) ? dp : 15555;
        var commandPort = int.TryParse(_config["Engine:Broker:NetMQ:CommandPort"], out var cp) ? cp : 15556;
        var resultsDir = Path.Combine(Path.GetTempPath(), "shamshir-backtest", runId);
        Directory.CreateDirectory(resultsDir);
        var reportJsonPath = Path.Combine(resultsDir, "report.json");
        var reportHtmlPath = Path.Combine(resultsDir, "report.html");

        var ctid = _config["CTrader:CtId"];
        var pwdFile = _config["CTrader:PwdFile"];
        var account = _config["CTrader:Account"];
        if (string.IsNullOrWhiteSpace(ctid) || string.IsNullOrWhiteSpace(pwdFile) || string.IsNullOrWhiteSpace(account))
        {
            return new BacktestResult
            {
                RunId = runId,
                ExitCode = 1,
                ErrorMessage = "CTrader credentials not configured. Set CTrader:CtId, CTrader:PwdFile, and CTrader:Account in appsettings or environment.",
            };
        }

        Process? engineProcess = null;
        var startSubprocess = string.Equals(_config["CTrader:StartEngineSubprocess"], "true", StringComparison.OrdinalIgnoreCase);
        if (startSubprocess)
        {
            engineProcess = StartEngine(dataPort, commandPort, runId);
            _logger.LogInformation("Engine subprocess started. PID={Pid}", engineProcess?.Id ?? -1);
            await WaitForEngineReadyAsync(commandPort, TimeSpan.FromSeconds(30), ct);
        }
        else
        {
            _logger.LogInformation("Using Aspire-managed engine. DataPort={DataPort} CommandPort={CommandPort}", dataPort, commandPort);
        }

        try
        {
            var cliPath = CTraderCliLocator.Locate(_config);
            var algoPath = ResolveAlgoPath();
            var args = BuildArgs(cfg, algoPath, dataPort, commandPort, reportJsonPath, reportHtmlPath);

            _logger.LogInformation("Launching ctrader-cli. RunId={RunId} Cmd={CliPath} {Args}", runId, cliPath, args);
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

            _logger.LogInformation("ctrader-cli exited. Code={Code} RunId={RunId}", cliProcess.ExitCode, runId);
            if (!string.IsNullOrWhiteSpace(stderr))
                _logger.LogWarning("ctrader-cli stderr: {Stderr}", stderr);
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                _logger.LogDebug("ctrader-cli stdout: {Stdout}", stdout);
                foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.Contains("CBOT|"))
                        _logger.LogInformation("cBot: {Line}", line.Trim());
                }
            }

            var isKnownCrash = cliProcess.ExitCode != 0
                && (stderr.Contains("Message expected") || stderr.Contains("Object reference")
                    || stdout.Contains("Message expected") || stdout.Contains("Object reference"));

            if (cliProcess.ExitCode != 0 && !isKnownCrash)
                _logger.LogError("ctrader-cli failed. Code={Code} Stderr={Stderr}", cliProcess.ExitCode, stderr);
            else if (isKnownCrash)
                _logger.LogWarning("ctrader-cli exited with known post-backtest crash (zero trades). Code={Code}", cliProcess.ExitCode);

            var errorMessage = ResolveError(cliProcess.ExitCode, stderr, stdout, isKnownCrash);

            var report = TryParseReport(reportJsonPath, _logger);

            return new BacktestResult
            {
                RunId           = runId,
                ExitCode        = isKnownCrash ? 0 : cliProcess.ExitCode,
                ErrorMessage    = errorMessage,
                ReportHtmlPath  = reportHtmlPath,
                NetProfit       = report.NetProfit,
                MaxDrawdownPct  = report.MaxDrawdownPct,
                TotalTrades     = report.TotalTrades,
                WinningTrades   = report.WinningTrades,
                WinRatePct      = report.WinRatePct,
            };
        }
        finally
        {
            if (engineProcess is not null && !engineProcess.HasExited)
            {
                engineProcess.Kill(entireProcessTree: true);
                await engineProcess.WaitForExitAsync(CancellationToken.None);
                _logger.LogInformation("Engine subprocess killed. PID={Pid}", engineProcess.Id);
            }
        }
    }

    private Process? StartEngine(int dataPort, int commandPort, string runId)
    {
        var baseDir = AppContext.BaseDirectory;
        var engineProjPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..",
            "src", "TradingEngine.Host", "TradingEngine.Host.csproj"));

        if (!File.Exists(engineProjPath))
        {
            _logger.LogWarning("Engine project not found at {Path} — engine subprocess skipped", engineProjPath);
            return null;
        }

        var psi = new ProcessStartInfo("dotnet", $"run --project \"{engineProjPath}\" --no-build")
        {
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment =
            {
                ["Engine__Mode"] = "Live",
                ["Engine__Broker__NetMQ__DataPort"] = dataPort.ToString(),
                ["Engine__Broker__NetMQ__CommandPort"] = commandPort.ToString(),
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
            },
        };

        var process = Process.Start(psi);
        _logger.LogInformation("Engine subprocess started. PID={Pid} Project={Project}", process?.Id, engineProjPath);
        return process;
    }

    private static async Task WaitForEngineReadyAsync(int commandPort, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var attempt = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", commandPort);
                return;
            }
            catch { }
            attempt++;
            await Task.Delay(300, ct);
        }
        throw new TimeoutException($"Engine command port {commandPort} not ready after {timeout.TotalSeconds:F0}s ({attempt} probes)");
    }

    private string BuildArgs(BacktestConfig cfg, string algoPath, int dataPort, int commandPort, string reportJsonPath, string reportHtmlPath)
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
        sb.Append($" --report=\"{reportHtmlPath}\"");
        sb.Append($" --ctid={_config["CTrader:CtId"]}");
        sb.Append($" --pwd-file=\"{_config["CTrader:PwdFile"]}\"");
        sb.Append($" --account={_config["CTrader:Account"]}");
        sb.Append($" --DataPort={dataPort}");
        sb.Append($" --CommandPort={commandPort}");
        sb.Append($" --SymbolString={string.Join(",", cfg.Symbols)}");
        sb.Append($" --Periods={string.Join(",", cfg.Periods)}");
        if (cfg.UseFullAccess)
            sb.Append(" --full-access");
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

    private static (decimal NetProfit, decimal MaxDrawdownPct, int TotalTrades, int WinningTrades, double WinRatePct)
        TryParseReport(string reportJsonPath, ILogger logger)
    {
        try
        {
            if (!File.Exists(reportJsonPath))
                return default;

            using var doc = JsonDocument.Parse(File.ReadAllText(reportJsonPath));
            var root = doc.RootElement;

            decimal netProfit = 0;
            if (root.TryGetProperty("NetProfit", out var np))
                netProfit = np.ValueKind == JsonValueKind.Number ? np.GetDecimal() : 0;

            var totalTrades = ReadInt(root, "TotalTrades");
            var winningTrades = ReadInt(root, "WinningTrades");

            decimal maxDd = 0;
            if (root.TryGetProperty("MaxEquityDrawdownPercentages", out var mdd) && mdd.ValueKind == JsonValueKind.Number)
                maxDd = mdd.GetDecimal() / 100m;

            double winRate = totalTrades > 0 ? (double)winningTrades / totalTrades : 0;

            return (netProfit, maxDd, totalTrades, winningTrades, winRate);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse ctrader-cli report JSON from {Path}", reportJsonPath);
            return default;
        }
    }

    private static int ReadInt(JsonElement root, string prop)
    {
        if (root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();
        return 0;
    }

    private static string? ResolveError(int exitCode, string stderr, string stdout, bool isKnownCrash)
    {
        if (exitCode == 0 || isKnownCrash) return null;

        if (!string.IsNullOrWhiteSpace(stderr))
            return stderr.Trim();

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                    return trimmed;
            }
            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 0)
                return lines[^1].Trim();
        }

        return $"ctrader-cli exited with code {exitCode} (no error output captured)";
    }
}

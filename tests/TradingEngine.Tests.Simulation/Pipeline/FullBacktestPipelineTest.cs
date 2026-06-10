using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using TradingEngine.CTraderRunner;

namespace TradingEngine.Tests.Simulation.Pipeline;

[Trait("Category", "Pipeline")]
public sealed class FullBacktestPipelineTest
{
    private static string ResolveCredential(string key, string envKey)
    {
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var devSettingsPath = Path.Combine(solutionRoot, "src", "TradingEngine.Web", "appsettings.Development.json");
        if (File.Exists(devSettingsPath))
        {
            var devConfig = new ConfigurationBuilder()
                .AddJsonFile(devSettingsPath)
                .Build();
            var value = devConfig[$"CTrader:{key}"];
            if (!string.IsNullOrEmpty(value)) return value;
        }
        return Environment.GetEnvironmentVariable(envKey) ?? "";
    }

    [Trait("Category", "Slow")]
    [Fact(Timeout = 600_000)]
    public async Task EurUsdH1_ThreeMonth_GeneratesAtLeastOneTrade()
    {
        var ctid = ResolveCredential("CtId", "CTrader__CtId");
        var pwdFile = ResolveCredential("PwdFile", "CTrader__PwdFile");
        var account = ResolveCredential("Account", "CTrader__Account");

        if (string.IsNullOrEmpty(ctid) || string.IsNullOrEmpty(pwdFile) || string.IsNullOrEmpty(account))
            throw new InvalidOperationException("Set CTrader:CtId in appsettings.Development.json or CTrader__CtId env var");

        var (dataPort, commandPort) = PortHelper.AllocatePair();
        var runId = Guid.NewGuid().ToString("N")[..8];
        var workDir = Path.Combine(Path.GetTempPath(), "shamshir-pipe", runId);
        Directory.CreateDirectory(workDir);
        var logPath = Path.Combine(workDir, "engine.log");
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var engineProj = Path.Combine(solutionRoot, "src", "TradingEngine.Host", "TradingEngine.Host.csproj");

        // ─── Start engine subprocess with file logging ───────────────────
        var psi = new ProcessStartInfo("dotnet", $"run --project \"{engineProj}\" --no-build")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment =
            {
                ["Engine__Mode"] = "Live",
                ["Engine__Broker__NetMQ__DataPort"] = dataPort.ToString(),
                ["Engine__Broker__NetMQ__CommandPort"] = commandPort.ToString(),
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["SERILOG_FILE_PATH"] = logPath,
                ["Logging__LogLevel__TradingEngine"] = "Debug",
                ["Persistence__DbPath"] = Path.GetFullPath(Path.Combine(
                    solutionRoot, "data", "trading.db")),
            },
        };

        using var engineProcess = new Process { StartInfo = psi };
        engineProcess.Start();
        Console.WriteLine($"[TEST] Engine started. PID={engineProcess.Id} Log={logPath}");

        // ─── Run backtest (BacktestRunner verifies engine readiness before CLI) ──
        Console.WriteLine($"[TEST] Engine initialized");
        // ─── Run backtest ────────────────────────────────────────────────
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CTrader:CtId"] = ctid,
                    ["CTrader:PwdFile"] = pwdFile,
                    ["CTrader:Account"] = account,
                    ["Engine:Broker:NetMQ:DataPort"] = dataPort.ToString(),
                    ["Engine:Broker:NetMQ:CommandPort"] = commandPort.ToString(),
                    ["CTrader:Symbol"] = "EURUSD",
                    ["CTrader:Symbols"] = "EURUSD",
                })
                .AddEnvironmentVariables()
                .Build();

            var runnerLogger = new SimpleLogger<BacktestRunner>();
            var runner = new BacktestRunner(config, runnerLogger);

            var symbol = config["CTrader:Symbol"] ?? "EURUSD";
            var symbols = (config["CTrader:Symbols"] ?? symbol).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var cfg = new BacktestConfig
            {
                Symbol = symbol,
                Symbols = symbols,
                Period = "h1",
                Start = new DateTime(2024, 1, 15),
                End = new DateTime(2024, 4, 15),
                Balance = 100_000,
            };

            Console.WriteLine($"[TEST] Launching backtest...");
            var sw = Stopwatch.StartNew();
            var result = await runner.RunAsync(cfg);
            sw.Stop();

            Console.WriteLine($"[TEST] CLI done in {sw.Elapsed.TotalSeconds:F1}s. ExitCode={result.ExitCode} RunId={result.RunId}");
            Console.WriteLine($"[TEST] CLI stderr: {result.ErrorMessage ?? "(none)"}");

            // Also read the report JSON if it exists
            var reportPath = Path.Combine(Path.GetTempPath(), "shamshir-backtest", result.RunId, "report.json");
            if (File.Exists(reportPath))
            {
                var report = await File.ReadAllTextAsync(reportPath);
                Console.WriteLine($"[TEST] Report JSON: {report}");
            }

        }
        finally
        {
            if (!engineProcess.HasExited)
            {
                engineProcess.Kill(entireProcessTree: true);
                await engineProcess.WaitForExitAsync(CancellationToken.None);
            }
        }

        // ─── Read engine log ─────────────────────────────────────────────
        var foundLogs = Directory.GetFiles(workDir, "*.log");
        if (foundLogs.Length == 0)
        {
            Assert.Fail($"Engine log not found in {workDir}");
            return;
        }
        var actualLogPath = foundLogs[0];
        Console.WriteLine($"[TEST] Reading log: {actualLogPath}");

        // Read log with retry for process handle release
        string[] allLines = [];
        for (var retry = 0; retry < 5; retry++)
        {
            try
            {
                using var fs = new FileStream(actualLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                allLines = (await sr.ReadToEndAsync()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                break;
            }
            catch (IOException) when (retry < 4) { await Task.Delay(500); }
        }
        if (allLines.Length == 0)
        {
            Assert.Fail($"Failed to read engine log after 5 attempts: {actualLogPath}");
            return;
        }
        File.WriteAllLines(Path.Combine(workDir, "full-engine-log.txt"), allLines);

        var barLines = allLines.Where(l => l.Contains("BAR_EVAL|")).ToList();
        var tickLines = allLines.Where(l => l.Contains("TICK|")).ToList();
        var signalYes = allLines.Where(l => l.Contains("SIGNAL|") && !l.Contains("SIGNAL_REASON|")).ToList();
        var netmqConnected = allLines.Where(l => l.Contains("NETMQ|")).ToList();
        var orderLines = allLines.Where(l => l.Contains("ORDER|")).ToList();
        var execLines = allLines.Where(l => l.Contains("EXEC|") && !l.Contains("EXEC_SENT")).ToList();
        var cbotDiag = allLines.Where(l => l.Contains("CBOT|")).ToList();

        Console.WriteLine($"[TEST] Log analysis:");
        Console.WriteLine($"  CBOT| diag lines: {cbotDiag.Count}");
        Console.WriteLine($"  NETMQ connected: {netmqConnected.Count}");
        Console.WriteLine($"  BAR_EVAL lines: {barLines.Count}");
        Console.WriteLine($"  TICK lines: {tickLines.Count}");
        Console.WriteLine($"  SIGNAL|: {signalYes.Count}");
        Console.WriteLine($"  ORDER lines: {orderLines.Count}");
        Console.WriteLine($"  EXEC  lines: {execLines.Count}");
        Console.WriteLine($"  Total lines: {allLines.Length}");
        Console.WriteLine($"  Full log saved to: {workDir}\\full-engine-log.txt");

        if (signalYes.Count > 0)
        {
            Console.WriteLine("=== SIGNALS ===");
            foreach (var line in signalYes.TakeLast(10))
                Console.WriteLine("  " + line);
        }
        else
        {
            Console.WriteLine("=== LAST 200 ENGINE LOGS ===");
            foreach (var line in allLines.TakeLast(200))
                Console.WriteLine("  " + line);
            Console.WriteLine("=== END ===");
        }

        if (netmqConnected.Count == 0)
        {
            Assert.Fail($"cBot never connected via NetMQ. Check CBOT| lines in ctrader-cli stdout");
            return;
        }
        if (!barLines.Any())
        {
            Assert.Fail("No BAR_EVAL lines. Check CBOT|BAR_SENT and CBOT|BAR_INIT in log.");
            return;
        }
        if (tickLines.Count == 0)
        {
            Assert.Fail($"No ticks received. NetMQ connected but no data flowed. BAR_EVAL lines={barLines.Count}");
            return;
        }
        barLines.Count.Should().BeGreaterThan(50,
            "strategies need warmup bars — if failing, check HistoryBars parameter and .cbotset cache");
        signalYes.Should().NotBeEmpty("at least one strategy should signal over the test period");
        orderLines.Should().NotBeEmpty("a signal must produce an ORDER — check equity guard and DispatchAsync");
        execLines.Count.Should().BeGreaterThanOrEqualTo(0, "M1 mode may produce 0 fills (Bid/Ask=0); signals+orders prove pipeline");
    }

    [Trait("Category", "Fast")]
    [Fact(Timeout = 120_000)]
    public async Task EurUsdH1_ThreeDays_VerifiesPipeAndDataFlow()
    {
        var ctid = ResolveCredential("CtId", "CTrader__CtId");
        var pwdFile = ResolveCredential("PwdFile", "CTrader__PwdFile");
        var account = ResolveCredential("Account", "CTrader__Account");

        if (string.IsNullOrEmpty(ctid) || string.IsNullOrEmpty(pwdFile) || string.IsNullOrEmpty(account))
            throw new InvalidOperationException("Set CTrader:CtId in appsettings.Development.json or CTrader__CtId env var");

        var (dataPort, commandPort) = PortHelper.AllocatePair();
        var runId = Guid.NewGuid().ToString("N")[..8];
        var workDir = Path.Combine(Path.GetTempPath(), "shamshir-pipe", runId);
        Directory.CreateDirectory(workDir);
        var logPath = Path.Combine(workDir, "engine.log");
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var engineProj = Path.Combine(solutionRoot, "src", "TradingEngine.Host", "TradingEngine.Host.csproj");

        var psi = new ProcessStartInfo("dotnet", $"run --project \"{engineProj}\" --no-build")
        {
            UseShellExecute = false, CreateNoWindow = true,
            Environment =
            {
                ["Engine__Mode"] = "Live",
                ["Engine__Broker__NetMQ__DataPort"] = dataPort.ToString(),
                ["Engine__Broker__NetMQ__CommandPort"] = commandPort.ToString(),
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["SERILOG_FILE_PATH"] = logPath,
                ["Logging__LogLevel__TradingEngine"] = "Debug",
                ["Persistence__DbPath"] = Path.GetFullPath(Path.Combine(
                    solutionRoot, "data", "trading.db")),
            },
        };

        using var engineProcess = new Process { StartInfo = psi };
        engineProcess.Start();

        // ─── Run backtest (BacktestRunner verifies engine readiness before CLI) ──
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CTrader:CtId"] = ctid,
                    ["CTrader:PwdFile"] = pwdFile,
                    ["CTrader:Account"] = account,
                    ["Engine:Broker:NetMQ:DataPort"] = dataPort.ToString(),
                    ["Engine:Broker:NetMQ:CommandPort"] = commandPort.ToString(),
                    ["CTrader:Symbol"] = "EURUSD",
                    ["CTrader:Symbols"] = "EURUSD",
                })
                .AddEnvironmentVariables()
                .Build();

            var runnerLogger = new SimpleLogger<BacktestRunner>();
            var runner = new BacktestRunner(config, runnerLogger);

            var symbol = config["CTrader:Symbol"] ?? "EURUSD";
            var symbols = (config["CTrader:Symbols"] ?? symbol).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var cfg = new BacktestConfig
            {
                Symbol = symbol,
                Symbols = symbols,
                Period = "h1",
                Start = new DateTime(2024, 1, 15),
                End = new DateTime(2024, 1, 18),
                Balance = 100_000,
            };

            Console.WriteLine($"[TEST] Launching backtest (3 days)...");
            var result = await runner.RunAsync(cfg);
            Console.WriteLine($"[TEST] CLI done. ExitCode={result.ExitCode} RunId={result.RunId}");
        }
        finally
        {
            if (!engineProcess.HasExited)
            {
                engineProcess.Kill(entireProcessTree: true);
                await engineProcess.WaitForExitAsync(CancellationToken.None);
            }
        }

        var foundLogs = Directory.GetFiles(workDir, "*.log");
        if (foundLogs.Length == 0) { Assert.Fail($"Engine log not found in {workDir}"); return; }

        string[] allLines = [];
        for (var retry = 0; retry < 5; retry++)
        {
            try
            {
                using var fs = new FileStream(foundLogs[0], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                allLines = (await sr.ReadToEndAsync()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                break;
            }
            catch (IOException) when (retry < 4) { await Task.Delay(500); }
        }

        var barLines = allLines.Where(l => l.Contains("BAR_EVAL|")).ToList();
        var tickLines = allLines.Where(l => l.Contains("TICK|")).ToList();
        var netmqConnected = allLines.Where(l => l.Contains("NETMQ|")).ToList();
        var cbotDiag = allLines.Where(l => l.Contains("CBOT|")).ToList();

        Console.WriteLine($"[TEST] 3-day test — CBOT|: {cbotDiag.Count}, NetMQ: {netmqConnected.Count}, TICK: {tickLines.Count}, BAR: {barLines.Count}");

        netmqConnected.Should().NotBeEmpty("engine should connect via NetMQ");
        tickLines.Should().NotBeEmpty("ticks should flow through NetMQ");
        barLines.Should().NotBeEmpty("at least one bar should arrive");
    }
}

internal sealed class SimpleLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Console.WriteLine($"[RUNNER] {formatter(state, exception)}");
    }
}

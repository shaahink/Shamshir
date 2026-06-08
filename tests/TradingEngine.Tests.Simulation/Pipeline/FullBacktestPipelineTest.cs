using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using TradingEngine.CTraderRunner;

namespace TradingEngine.Tests.Simulation.Pipeline;

[Trait("Category", "Pipeline")]
public sealed class FullBacktestPipelineTest
{
    [Trait("Category", "Slow")]
    [Fact(Timeout = 600_000)]
    public async Task EurUsdH1_ThreeMonth_GeneratesAtLeastOneTrade()
    {
        var ctid = Environment.GetEnvironmentVariable("CTrader__CtId");
        var pwdFile = Environment.GetEnvironmentVariable("CTrader__PwdFile");
        var account = Environment.GetEnvironmentVariable("CTrader__Account");

        if (string.IsNullOrEmpty(ctid) || string.IsNullOrEmpty(pwdFile) || string.IsNullOrEmpty(account))
            throw new InvalidOperationException("Set CTrader__CtId, CTrader__PwdFile, CTrader__Account env vars first");

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
                ["Engine__Broker__NetMQ__DataPort"] = "15555",
                ["Engine__Broker__NetMQ__CommandPort"] = "15556",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["SERILOG_FILE_PATH"] = logPath,
            },
        };

        using var engineProcess = new Process { StartInfo = psi };
        engineProcess.Start();
        Console.WriteLine($"[TEST] Engine started. PID={engineProcess.Id} Log={logPath}");

        // ─── Wait for engine to initialize ────────────────────────────
        await Task.Delay(3000);
        if (engineProcess.HasExited)
        {
            engineProcess.Kill(entireProcessTree: true);
            await engineProcess.WaitForExitAsync(CancellationToken.None);
            var logFiles = Directory.GetFiles(workDir, "*.log");
            var logContent = logFiles.Length > 0 ? await File.ReadAllTextAsync(logFiles[0]) : "(no log file)";
            var output = $"[TEST] Engine exited prematurely. ExitCode={engineProcess.ExitCode}\n\nEngine log:\n{logContent}";
            File.WriteAllText(Path.Combine(workDir, "test-output.txt"), output);
            Console.WriteLine(output);
            Assert.Fail($"Engine exited during startup. See {workDir}\\test-output.txt");
            return;
        }
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
                    ["Engine:Broker:NetMQ:DataPort"] = "15555",
                    ["Engine:Broker:NetMQ:CommandPort"] = "15556",
                })
                .AddEnvironmentVariables()
                .Build();

            var runnerLogger = new SimpleLogger<BacktestRunner>();
            var runner = new BacktestRunner(config, runnerLogger);

            var cfg = new BacktestConfig
            {
                Symbol = "EURUSD",
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

        Console.WriteLine($"[TEST] Log analysis:");
        Console.WriteLine($"  NETMQ connected: {netmqConnected.Count}");
        Console.WriteLine($"  BAR_EVAL lines: {barLines.Count}");
        Console.WriteLine($"  TICK lines: {tickLines.Count}");
        Console.WriteLine($"  SIGNAL|: {signalYes.Count}");
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
        if (tickLines.Count == 0)
        {
            Assert.Fail($"No ticks received. NetMQ connected but no data flowed. BAR_EVAL lines={barLines.Count}");
            return;
        }
        barLines.Should().NotBeEmpty("bars must arrive before strategies can evaluate");
        signalYes.Should().NotBeEmpty("at least one strategy should generate a signal over 3 months of H1 EURUSD data");
    }

    [Trait("Category", "Fast")]
    [Fact(Timeout = 120_000)]
    public async Task EurUsdH1_ThreeDays_VerifiesPipeAndDataFlow()
    {
        var ctid = Environment.GetEnvironmentVariable("CTrader__CtId");
        var pwdFile = Environment.GetEnvironmentVariable("CTrader__PwdFile");
        var account = Environment.GetEnvironmentVariable("CTrader__Account");

        if (string.IsNullOrEmpty(ctid) || string.IsNullOrEmpty(pwdFile) || string.IsNullOrEmpty(account))
            throw new InvalidOperationException("Set CTrader__CtId, CTrader__PwdFile, CTrader__Account env vars first");

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
                ["Engine__Broker__NetMQ__DataPort"] = "15555",
                ["Engine__Broker__NetMQ__CommandPort"] = "15556",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["SERILOG_FILE_PATH"] = logPath,
            },
        };

        using var engineProcess = new Process { StartInfo = psi };
        engineProcess.Start();

        await Task.Delay(3000);
        if (engineProcess.HasExited)
        {
            engineProcess.Kill(entireProcessTree: true);
            await engineProcess.WaitForExitAsync(CancellationToken.None);
            var logFiles = Directory.GetFiles(workDir, "*.log");
            var logContent = logFiles.Length > 0 ? await File.ReadAllTextAsync(logFiles[0]) : "(no log file)";
            Console.WriteLine($"[TEST] Engine exited prematurely. ExitCode={engineProcess.ExitCode}\n\nEngine log:\n{logContent}");
            Assert.Fail($"Engine exited during startup");
            return;
        }

        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CTrader:CtId"] = ctid,
                    ["CTrader:PwdFile"] = pwdFile,
                    ["CTrader:Account"] = account,
                    ["Engine:Broker:NetMQ:DataPort"] = "15555",
                    ["Engine:Broker:NetMQ:CommandPort"] = "15556",
                })
                .AddEnvironmentVariables()
                .Build();

            var runnerLogger = new SimpleLogger<BacktestRunner>();
            var runner = new BacktestRunner(config, runnerLogger);

            var cfg = new BacktestConfig
            {
                Symbol = "EURUSD",
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

        Console.WriteLine($"[TEST] 3-day test — NetMQ connected: {netmqConnected.Count}, TICK: {tickLines.Count}, BAR: {barLines.Count}");

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

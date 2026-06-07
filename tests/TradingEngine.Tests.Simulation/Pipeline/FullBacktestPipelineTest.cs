using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using TradingEngine.CTraderRunner;

namespace TradingEngine.Tests.Simulation.Pipeline;

[Trait("Category", "Pipeline")]
public sealed class FullBacktestPipelineTest
{
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
        var pipeName = $"shamshir-{runId}";
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
                ["Engine__Broker__PipeName"] = pipeName,
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["SERILOG_FILE_PATH"] = logPath,
            },
        };

        using var engineProcess = new Process { StartInfo = psi };
        engineProcess.Start();
        Console.WriteLine($"[TEST] Engine started. PID={engineProcess.Id} Log={logPath}");

        // ─── Wait for pipe to be created ────────────────────────────────
        var pipePath = Path.Combine(@"\\.\pipe", pipeName);
        var readyDeadline = DateTime.UtcNow.AddSeconds(60);
        var pipeFound = false;
        while (DateTime.UtcNow < readyDeadline)
        {
            if (engineProcess.HasExited)
            {
                Console.WriteLine($"[TEST] Engine exited prematurely. ExitCode={engineProcess.ExitCode}");
                break;
            }
            if (File.Exists(pipePath)) { pipeFound = true; break; }
            await Task.Delay(500);
        }

        if (!pipeFound)
        {
            engineProcess.Kill(entireProcessTree: true);
            await engineProcess.WaitForExitAsync(CancellationToken.None);

            var logFiles = Directory.GetFiles(workDir, "*.log");
            var logContent = logFiles.Length > 0 ? await File.ReadAllTextAsync(logFiles[0]) : "(no log file)";
            var output = $"[TEST] Engine pipe not found within 60s. Exited={(engineProcess.HasExited ? "yes" : "no")}\n\nEngine log:\n{logContent}";
            File.WriteAllText(Path.Combine(workDir, "test-output.txt"), output);
            Console.WriteLine(output);
            Assert.Fail($"Engine pipe '{pipeName}' not found within 60s. See {workDir}\\test-output.txt");
            return;
        }
        Console.WriteLine($"[TEST] Pipe ready after ~{(DateTime.UtcNow.AddSeconds(-60)).ToString()}");
        // ─── Run backtest ────────────────────────────────────────────────
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CTrader:CtId"] = ctid,
                    ["CTrader:PwdFile"] = pwdFile,
                    ["CTrader:Account"] = account,
                    ["Engine:Broker:PipeName"] = pipeName,
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

        var barLines = allLines.Where(l => l.Contains("BAR|")).ToList();
        var tickLines = allLines.Where(l => l.Contains("TICK|")).ToList();
        var signalYes = allLines.Where(l => l.Contains("SIGNAL|") && l.Contains("|YES|")).ToList();
        var signalNo = allLines.Where(l => l.Contains("SIGNAL|") && l.Contains("|NO|")).ToList();
        var needBars = allLines.Where(l => l.Contains("NEED_BARS")).ToList();
        var skips = allLines.Where(l => l.Contains("SKIP|")).ToList();
        var pipeConnected = allLines.Where(l => l.Contains("Pipe connected")).ToList();

        Console.WriteLine($"[TEST] Log analysis:");
        Console.WriteLine($"  Pipe connected: {pipeConnected.Count}");
        Console.WriteLine($"  BAR lines: {barLines.Count}");
        Console.WriteLine($"  TICK lines: {tickLines.Count}");
        Console.WriteLine($"  SIGNAL|YES: {signalYes.Count}");
        Console.WriteLine($"  SIGNAL|NO: {signalNo.Count}");
        Console.WriteLine($"  NEED_BARS: {needBars.Count}");
        Console.WriteLine($"  SKIP: {skips.Count}");
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

        signalYes.Should().NotBeEmpty("at least one strategy should generate a signal over 3 months of H1 EURUSD data");
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

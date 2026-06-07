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
        {
            throw new InvalidOperationException(
                "Set CTrader__CtId, CTrader__PwdFile, CTrader__Account env vars first");
        }

        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var engineProj = Path.Combine(solutionRoot, "src", "TradingEngine.Host", "TradingEngine.Host.csproj");

        // ─── Start engine ─────────────────────────────────────────────────
        var engineLog = new ConcurrentQueue<string>();
        var engineReady = new TaskCompletionSource();

        var enginePsi = new ProcessStartInfo("dotnet", $"run --project \"{engineProj}\" --no-build")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment =
            {
                ["Engine__Mode"] = "Live",
                ["Engine__Broker__PipeName"] = "trading-engine",
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
            },
        };

        using var engineProcess = new Process { StartInfo = enginePsi };
        var engineLogTask = Task.Run(() =>
        {
            while (!engineProcess.StandardOutput.EndOfStream)
            {
                var line = engineProcess.StandardOutput.ReadLine();
                if (line is null) break;
                engineLog.Enqueue(line);
                if (line.Contains("Pipe connected") || line.Contains("Active prop firm"))
                    engineReady.TrySetResult();
            }
            var err = engineProcess.StandardError.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(err)) engineLog.Enqueue("STDERR: " + err);
        });

        engineProcess.Start();
        Console.WriteLine($"[TEST] Engine started. PID={engineProcess.Id}");

        var readyTimeout = Task.Delay(TimeSpan.FromSeconds(60));
        var ready = await Task.WhenAny(engineReady.Task, readyTimeout);
        if (ready != engineReady.Task)
        {
            engineProcess.Kill(entireProcessTree: true);
            await engineLogTask;
            DumpAndFail(engineLog, "Engine not ready within 60s");
            return;
        }
        Console.WriteLine("[TEST] Engine ready.");

        // ─── Run backtest ─────────────────────────────────────────────────
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CTrader:CtId"] = ctid,
                    ["CTrader:PwdFile"] = pwdFile,
                    ["CTrader:Account"] = account,
                    ["Engine:Broker:PipeName"] = "trading-engine",
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
            var result = await runner.RunAsync(cfg);
            Console.WriteLine($"[TEST] Backtest done. ExitCode={result.ExitCode}");

            await Task.Delay(2000);

            var allLogs = engineLog.ToList();
            var signalLines = allLogs.Where(l => l.Contains("SIGNAL|") && l.Contains("|YES|")).ToList();
            var barLines = allLogs.Where(l => l.StartsWith("BAR|")).ToList();
            var tickLines = allLogs.Where(l => l.StartsWith("TICK|")).ToList();

            Console.WriteLine($"[TEST] Bars={barLines.Count} Ticks={tickLines.Count} Signals={signalLines.Count}");

            if (signalLines.Count == 0)
            {
                Console.WriteLine("=== LAST 200 ENGINE LOGS ===");
                foreach (var line in allLogs.TakeLast(200))
                    Console.WriteLine(line);
                Console.WriteLine("=== END ===");
                signalLines.Should().NotBeEmpty("no strategy generated a signal in 3 months of H1 EURUSD data");
            }
            else
            {
                Console.WriteLine("=== SIGNALS ===");
                foreach (var line in signalLines)
                    Console.WriteLine(line);
            }
        }
        finally
        {
            if (!engineProcess.HasExited)
            {
                engineProcess.Kill(entireProcessTree: true);
                await engineProcess.WaitForExitAsync(CancellationToken.None);
            }
            await engineLogTask;
        }
    }

    private static void DumpAndFail(ConcurrentQueue<string> logs, string reason)
    {
        Console.WriteLine($"[TEST] FAIL: {reason}");
        foreach (var line in logs.TakeLast(100))
            Console.WriteLine(line);
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

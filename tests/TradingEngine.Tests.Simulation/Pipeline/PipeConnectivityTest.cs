using System.Diagnostics;
using System.IO.Pipes;
using Newtonsoft.Json;

namespace TradingEngine.Tests.Simulation.Pipeline;

[Trait("Category", "Pipe")]
public sealed class PipeConnectivityTest
{
    [Fact(Timeout = 30_000)]
    public async Task EngineAcceptsPipeConnection_FromTestProcess()
    {
        var runId = Guid.NewGuid().ToString("N")[..8];
        var pipeName = $"shamshir-pipe-{runId}";
        var workDir = Path.Combine(Path.GetTempPath(), "shamshir-pipe", runId);
        Directory.CreateDirectory(workDir);
        var logPath = Path.Combine(workDir, "engine.log");
        var solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var engineProj = Path.Combine(solutionRoot, "src", "TradingEngine.Host", "TradingEngine.Host.csproj");

        using var engineProcess = Process.Start(new ProcessStartInfo("dotnet",
            $"run --project \"{engineProj}\" --no-build")
        {
            UseShellExecute = false, CreateNoWindow = true,
            Environment =
            {
                ["Engine__Mode"] = "Live",
                ["Engine__Broker__PipeName"] = pipeName,
                ["SERILOG_FILE_PATH"] = logPath,
            },
        })!;

        Console.WriteLine($"[PipeTest] Engine PID={engineProcess.Id} Log={logPath}");

        try
        {
            var ready = false;
            for (var i = 0; i < 30 && !ready; i++)
            {
                if (engineProcess.HasExited)
                {
                    Console.WriteLine($"[PipeTest] Engine exited prematurely. ExitCode={engineProcess.ExitCode}");
                    if (File.Exists(logPath))
                        Console.WriteLine($"[PipeTest] Engine log:\n{await File.ReadAllTextAsync(logPath)}");
                    break;
                }
                try
                {
                    using var probe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    probe.Connect(200);
                    ready = true;
                }
                catch { }
                await Task.Delay(500);
            }

            ready.Should().BeTrue("engine pipe server should be ready within 15s");

            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(2000);
            client.IsConnected.Should().BeTrue();

            var msgJson = JsonConvert.SerializeObject(new
            {
                Type = "Tick",
                Payload = new { Symbol = "EURUSD", Bid = 1.08500m, Ask = 1.08502m, TimestampUtc = DateTime.UtcNow.ToString("o") }
            });
            var utf8 = System.Text.Encoding.UTF8.GetBytes(msgJson);
            var lengthBytes = BitConverter.GetBytes(utf8.Length);
            await client.WriteAsync(lengthBytes);
            await client.WriteAsync(utf8);
            await client.FlushAsync();
            await Task.Delay(1000);
        }
        finally
        {
            if (!engineProcess.HasExited) engineProcess.Kill(entireProcessTree: true);
            await engineProcess.WaitForExitAsync(CancellationToken.None);
        }

        await Task.Delay(1000);
        var foundLogs = Directory.GetFiles(workDir, "*.log");
        if (foundLogs.Length > 0)
        {
            var actualLogPath = foundLogs[0];
            Console.WriteLine($"[PipeTest] Log file found: {actualLogPath} ({new FileInfo(actualLogPath).Length} bytes)");
            var lines = await File.ReadAllLinesAsync(actualLogPath);
            Console.WriteLine($"[PipeTest] Log lines: {lines.Length}");
            foreach (var line in lines.Where(l => l.Contains("PIPE") || l.Contains("TICK")))
                Console.WriteLine($"  {line}");

            var pipeConnected = lines.Any(l => l.Contains("PIPE_SERVER|CLIENT_CONNECTED") || l.Contains("Pipe connected"));
            var tickLogged = lines.Any(l => l.Contains("TICK|EURUSD"));

            Console.WriteLine($"[PipeTest] Pipe connected: {pipeConnected}");
            Console.WriteLine($"[PipeTest] Tick logged: {tickLogged}");

            pipeConnected.Should().BeTrue("engine should log a client connection event");
            tickLogged.Should().BeTrue("engine should log the TICK| line after processing");
        }
        else
        {
            Console.WriteLine($"[PipeTest] No log file found in {workDir}");
            if (File.Exists(logPath))
                Console.WriteLine($"[PipeTest] Exact path exists: {logPath}");
            Assert.Fail("Engine log file not found");
        }
    }
}

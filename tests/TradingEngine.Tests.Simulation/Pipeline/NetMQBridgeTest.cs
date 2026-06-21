using System.Diagnostics;
using System.IO.Pipes;
using NetMQ;
using NetMQ.Sockets;
using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Pipeline;

[Trait("Category", "NetMQ")]
[Trait("RequiresCTrader", "true")]
public sealed class NetMQBridgeTest
{
    private static bool HasCredentials =>
        !string.IsNullOrEmpty(CtraderTestHelpers.ResolveCredential("CtId", "CTrader__CtId"));

    [SkippableFact(Timeout = 20_000)]
    public async Task EngineReceivesBarAndTickOverNetMQ()
    {
        // iter-38 CT-1: process+socket-heavy, gated with the live cTrader env (RequiresCTrader trait).
        // SKIP rather than fail in the credential-free CI.
        Skip.IfNot(HasCredentials, "No cTrader credentials — see .claude/skills/ctrader-e2e (CT-1).");

        var (dataPort, commandPort) = PortHelper.AllocatePair();
        var workDir = Path.Combine(Path.GetTempPath(), "shamshir-mq", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        var logPath = Path.Combine(workDir, "engine.log");
        var solRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var projPath = Path.Combine(solRoot, "src", "TradingEngine.Host", "TradingEngine.Host.csproj");

        using var engine = Process.Start(new ProcessStartInfo("dotnet",
            $"run --project \"{projPath}\" --no-build")
        {
            UseShellExecute = false, CreateNoWindow = true,
            Environment =
            {
                ["Engine__Mode"] = "Live",
                ["Engine__Broker__Type"] = "NetMQ",
                ["Engine__Broker__NetMQ__DataPort"] = dataPort.ToString(),
                ["Engine__Broker__NetMQ__CommandPort"] = commandPort.ToString(),
                ["SERILOG_FILE_PATH"] = logPath,
            },
        })!;

        try
        {
            var ready = false;
            for (var i = 0; i < 40 && !ready; i++)
            {
                await Task.Delay(250);
                try
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    await tcp.ConnectAsync("127.0.0.1", commandPort);
                    ready = true;
                }
                catch { }
            }
            ready.Should().BeTrue("engine ROUTER should bind within 10s");

            using var pub = new PublisherSocket();
            using var dealer = new DealerSocket();
            pub.Bind($"tcp://*:{dataPort}");
            await Task.Delay(500);

            dealer.Connect($"tcp://127.0.0.1:{commandPort}");
            await Task.Delay(200);
            dealer.SendFrame("""{"type":"hello"}""");

            // Wait for NETMQ|CONNECTED in log before sending data
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(200);
                try
                {
                    var logFiles = Directory.GetFiles(workDir, "*.log");
                    if (logFiles.Length > 0 && (await File.ReadAllTextAsync(logFiles[0])).Contains("NETMQ|CONNECTED"))
                        break;
                }
                catch { }
            }

            // Send bar multiple times to overcome PUB/SUB slow joiner
            for (var i = 0; i < 5; i++)
            {
                var barJson = """{"type":"bar","symbol":"EURUSD","period":"H1","openTime":"2024-01-15T00:00:00Z","open":1.09000,"high":1.09500,"low":1.08800,"close":1.09200,"volume":1000}""";
                pub.SendMoreFrame("bar").SendFrame(barJson);
                await Task.Delay(100);
            }

            var tickJson = """{"type":"tick","symbol":"EURUSD","bid":1.09200,"ask":1.09202,"time":"2024-01-15T01:00:00Z"}""";
            pub.SendMoreFrame("tick").SendFrame(tickJson);

            await Task.Delay(2000);
        }
        finally
        {
            if (!engine.HasExited) engine.Kill(entireProcessTree: true);
            await engine.WaitForExitAsync(CancellationToken.None);
        }

        await Task.Delay(1000);
        var foundLogs = Directory.GetFiles(workDir, "*.log");
        var lines = foundLogs.Length > 0 ? await File.ReadAllLinesAsync(foundLogs[0]) : [];
        Console.WriteLine($"[TEST] Log lines: {lines.Length} from {foundLogs.FirstOrDefault()}");
        foreach (var l in lines.Where(l => l.Contains("BAR") || l.Contains("TICK") || l.Contains("NETMQ") || l.Contains("CONNECTED") || l.Contains("PARSE") || l.Contains("ERR")))
            Console.WriteLine($"  {l}");

        if (lines.Length == 0)
            Assert.Fail($"Engine log not found in {workDir}");

        lines.Should().Contain(l => l.Contains("NETMQ") && l.Contains("CONNECTED"),
            "engine should log identity capture");
        lines.Should().Contain(l => l.Contains("BAR_EVAL") && l.Contains("EURUSD"),
            "engine should log BAR_EVAL after receiving bar");
        lines.Should().Contain(l => l.Contains("TICK|EURUSD"),
            "engine should log TICK after receiving tick");

        try { Directory.Delete(workDir, true); } catch { }
    }
}

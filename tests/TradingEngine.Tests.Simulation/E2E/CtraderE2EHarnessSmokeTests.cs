using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.E2E;

[Trait("Category", "E2E")]
[Trait("Category", "Slow")]
[Trait("RequiresCTrader", "true")]
[Collection("CtraderSerial")]
public sealed class CtraderE2EHarnessSmokeTests
{
    private static bool HasCredentials =>
        !string.IsNullOrEmpty(CtraderTestHarness.ResolveCredential("CtId", "CTrader__CtId"));

    [Fact(Timeout = 300_000)]
    public async Task EurUsd_H1_3Days_ProducesTrades_UsingPhasedHarness()
    {
        if (!HasCredentials)
        {
            Console.WriteLine("[E2E-Smoke] No cTrader credentials — skipping");
            return;
        }

        await using var harness = new CtraderE2EHarness("smoke-phased-3d")
            .WithSymbol("EURUSD", "H1")
            .WithDateRange(new DateTime(2024, 1, 15), new DateTime(2024, 1, 18));

        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        await harness.StartEngineAsync(cts.Token);
        Console.WriteLine($"[{harness.RunId}] Engine started");

        await harness.StartCtraderAsync(cts.Token);
        Console.WriteLine($"[{harness.RunId}] cTrader CLI finished");

        await harness.WaitForHandshakeAsync(TimeSpan.FromSeconds(30), cts.Token);
        Console.WriteLine($"[{harness.RunId}] Handshake complete. Phase={harness.TransportStatus?.Current.Phase}");

        await harness.WaitForCompletionAsync(TimeSpan.FromMinutes(4), cts.Token);
        Console.WriteLine($"[{harness.RunId}] Completion reached");

        var result = harness.CollectResult();
        Console.WriteLine($"[{harness.RunId}] Trades={result.Trades} BarEvals={result.BarEvals}");

        result.Trades.Should().BeGreaterThan(0, "3 days EURUSD H1 should produce at least one trade in phased mode");
        result.FinalTransportStatus.Should().NotBeNull();
    }

    [Fact(Timeout = 300_000)]
    public async Task EurUsd_H1_3Days_ProducesTrades_UsingRunAsync()
    {
        if (!HasCredentials)
        {
            Console.WriteLine("[E2E-Smoke] No cTrader credentials — skipping");
            return;
        }

        var result = await new CtraderE2EHarness("smoke-runasync-3d")
            .WithSymbol("EURUSD", "H1")
            .WithDateRange(new DateTime(2024, 1, 15), new DateTime(2024, 1, 18))
            .RunAsync();

        Console.WriteLine($"[{result.RunId}] Trades={result.Trades} BarEvals={result.BarEvals}");
        Console.WriteLine($"[{result.RunId}] Transport phase={result.FinalTransportStatus?.Phase}");

        result.BarEvals.Should().BeGreaterThan(0, "bars should flow through the pipeline");
        result.Trades.Should().BeGreaterThan(0, "at least one trade expected in 3 days");
    }
}

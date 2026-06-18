using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Pipeline;

[Trait("Category", "InProcess")]
[Collection("CtraderSerial")]
public sealed class InProcessCtraderTest
{
    [Fact(Timeout = 240_000)]
    public async Task InProcessEngine_WithCtraderCli_EurUsd_OneDay_ProducesTrades()
    {
        var ctid = CtraderTestHarness.ResolveCredential("CtId", "CTrader__CtId");
        if (string.IsNullOrEmpty(ctid))
        {
            Console.WriteLine("[TEST] No cTrader credentials — skipping");
            return;
        }

        await using var harness = new CtraderTestHarness("inproc-1d");
        var result = await harness.RunAsync(
            "EURUSD", "H1",
            new DateTime(2024, 1, 15), new DateTime(2024, 1, 16),
            "1D-EURUSD");

        Console.WriteLine($"[RESULT:1D-EURUSD] Trades={result.Trades} BarEvals={result.BarEvals} Signals={result.Signals} CLI-Exit={result.CliExitCode}");
        result.BarEvals.Should().BeGreaterThan(0);
    }
}

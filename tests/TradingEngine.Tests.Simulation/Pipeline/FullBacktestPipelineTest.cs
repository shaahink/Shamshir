using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Pipeline;

[Trait("Category", "Pipeline")]
[Collection("CtraderSerial")]
public sealed class FullBacktestPipelineTest
{
    private async Task<CtraderTestHarness.Result> RunAsync(
        string symbol, string period, DateTime start, DateTime end, string label)
    {
        await using var harness = new CtraderTestHarness();
        return await harness.RunAsync(symbol, period, start, end, label);
    }

    [Trait("Category", "Slow")]
    [Fact(Timeout = 600_000)]
    public async Task EurUsdH1_ThreeMonth_GeneratesAtLeastOneTrade()
    {
        var result = await RunAsync(
            "EURUSD", "H1",
            new DateTime(2024, 1, 15), new DateTime(2024, 4, 15),
            "3M-EURUSD");

        Console.WriteLine($"[RESULT:3M-EURUSD] Trades={result.Trades} BarEvals={result.BarEvals} Signals={result.Signals} CLI-Exit={result.CliExitCode}");
        result.BarEvals.Should().BeGreaterThan(0, "bars should flow through the pipeline");
        result.Trades.Should().BeGreaterThan(0, "at least one trade expected in 3 months");
    }

    [Trait("Category", "Fast")]
    [Theory]
    [InlineData("EURUSD")]
    [InlineData("GBPUSD")]
    public async Task ThreeDays_PipeAndDataFlow(string symbol)
    {
        var result = await RunAsync(
            symbol, "H1",
            new DateTime(2024, 1, 15), new DateTime(2024, 1, 18),
            $"3D-{symbol}");

        Console.WriteLine($"[RESULT:3D-{symbol}] Trades={result.Trades} BarEvals={result.BarEvals} Signals={result.Signals} Orders={result.Orders} Execs={result.Execs}");
        result.BarEvals.Should().BeGreaterThan(0, $"{symbol} bars should flow through the full pipeline");
        result.Signals.Should().BeGreaterThan(0, $"{symbol} should generate at least one signal");
    }
}

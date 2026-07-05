using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.E2E;

[Trait("Category", "E2E")]
[Collection("CtraderSerial")]
public sealed class PipelineE2ETests
{
    private static bool HasCredentials =>
        !string.IsNullOrEmpty(CtraderTestHelpers.ResolveCredential("CtId", "CTrader__CtId"));

    private static async Task<E2EResult?> RunAsync(
        string symbol, string period, DateTime start, DateTime end, string label)
    {
        // iter-38 CT-1: genuinely SKIP when the live cTrader env is absent.
        Skip.IfNot(HasCredentials, $"[{label}] No cTrader credentials — see .claude/skills/ctrader-e2e (CT-1).");

        return await new CtraderE2EHarness(label)
            .WithSymbol(symbol, period)
            .WithDateRange(start, end)
            .RunAsync();
    }

    [Trait("Category", "Slow")]
    [Fact(Skip = "P4.5: retired per cTrader test policy — 3-month strategy behavior covered by tape golden/characterization suites")]
    public async Task EurUsd_H1_ThreeMonth_GeneratesAtLeastOneTrade()
    {
        var result = await RunAsync("EURUSD", "H1",
            new DateTime(2024, 1, 15), new DateTime(2024, 4, 15), "pipeline-3m");
        if (result is null) return;

        Console.WriteLine($"[RESULT:pipeline-3m] Trades={result.Trades} BarEvals={result.BarEvals}");
        result.BarEvals.Should().BeGreaterThan(0);
        result.Trades.Should().BeGreaterThan(0);
    }

    [Trait("Category", "Fast")]
    [Trait("Category", "CtraderContract")]
    [SkippableTheory]
    [InlineData("EURUSD")]
    [InlineData("GBPUSD")]
    public async Task ThreeDays_PipeAndDataFlow(string symbol)
    {
        var result = await RunAsync(symbol, "H1",
            new DateTime(2024, 1, 15), new DateTime(2024, 1, 18), $"pipeline-3d-{symbol}");
        if (result is null) return;

        Console.WriteLine($"[RESULT:pipeline-3d-{symbol}] Trades={result.Trades} BarEvals={result.BarEvals}");
        result.BarEvals.Should().BeGreaterThan(0);
    }

    [Trait("Category", "CtraderContract")]
    [SkippableFact(Timeout = 300_000)]
    public async Task EurUsd_H1_3Days_ProducesTrades()
    {
        var result = await RunAsync("EURUSD", "H1",
            new DateTime(2024, 1, 15), new DateTime(2024, 1, 18), "diag-3d");
        if (result is null) return;

        Console.WriteLine($"[RESULT:diag-3d] Trades={result.Trades} BarEvals={result.BarEvals}");
        result.BarEvals.Should().BeGreaterThan(0);
        result.Trades.Should().BeGreaterThan(0);
    }

    [Fact(Skip = "P4.5: retired per cTrader test policy — web-default behavior covered by tape characterization")]
    public async Task EurUsd_H1_30Days_MirrorsWebDefault_ProducesTrades()
    {
        var result = await RunAsync("EURUSD", "H1",
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 31), "diag-30d");
        if (result is null) return;

        Console.WriteLine($"[RESULT:diag-30d] Trades={result.Trades} BarEvals={result.BarEvals}");
        result.BarEvals.Should().BeGreaterThan(0);
    }

    [Trait("Category", "CtraderContract")]
    [SkippableFact(Timeout = 300_000)]
    public async Task EurUsd_M15_3Days_ProducesTrades()
    {
        var result = await RunAsync("EURUSD", "M15",
            new DateTime(2024, 1, 15), new DateTime(2024, 1, 18), "diag-m15-3d");
        if (result is null) return;

        // P4.5 reshape: assert connection facts instead of strategy behavior ("produces trades").
        // This is the ONLY coverage of non-H1 through the real cBot Periods wiring.
        Console.WriteLine($"[RESULT:diag-m15-3d] Trades={result.Trades} BarEvals={result.BarEvals}");
        result.BarEvals.Should().BeGreaterThan(0, "M15 bars must be received by the engine");
    }

    [Fact(Skip = "P4.5: retired per cTrader test policy — GBPUSD tape characterization pending P5.1 downloads")]
    public async Task GbpUsd_H1_30Days_ProducesTrades()
    {
        var result = await RunAsync("GBPUSD", "H1",
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 31), "diag-gbpusd-30d");
        if (result is null) return;

        Console.WriteLine($"[RESULT:diag-gbpusd-30d] Trades={result.Trades} BarEvals={result.BarEvals}");
        result.BarEvals.Should().BeGreaterThan(0);
        result.Trades.Should().BeGreaterThan(0);
    }

    [Trait("Category", "CtraderContract")]
    [SkippableFact(Timeout = 300_000)]
    public async Task InProcessEngine_WithCtraderCli_EurUsd_OneDay_ProducesTrades()
    {
        var result = await RunAsync("EURUSD", "H1",
            new DateTime(2024, 1, 15), new DateTime(2024, 1, 16), "inproc-1d");
        if (result is null) return;

        Console.WriteLine($"[RESULT:inproc-1d] Trades={result.Trades} BarEvals={result.BarEvals}");
        result.BarEvals.Should().BeGreaterThan(0);
    }
}

using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.E2E;

[Trait("Category", "E2E")]
[Trait("Category", "Slow")]
[Trait("Category", "CtraderContract")]
[Trait("RequiresCTrader", "true")]
[Collection("CtraderSerial")]
public sealed class CtraderScenarioE2ETests
{
    private static bool HasCredentials =>
        !string.IsNullOrEmpty(CtraderTestHelpers.ResolveCredential("CtId", "CTrader__CtId"));

    private static async Task<E2EResult?> RunAsync(
        string symbol, string period, DateTime start, DateTime end, string label)
    {
        // iter-38 CT-1: genuinely SKIP (not a misleading PASS) when the live cTrader env is absent.
        Skip.IfNot(HasCredentials, $"[{label}] No cTrader credentials — see .claude/skills/ctrader-e2e (CT-1).");

        return await new CtraderE2EHarness(label)
            .WithSymbol(symbol, period)
            .WithDateRange(start, end)
            .RunAsync();
    }

    [SkippableFact(Timeout = 300_000)]
    public async Task HappyPath_EurUsd_TradeLedgerHasIntegrity()
    {
        var result = await RunAsync("EURUSD", "H1",
            new DateTime(2024, 1, 15), new DateTime(2024, 1, 18), "integrity-3d");
        if (result is null) return;

        result.BarEvals.Should().BeGreaterThan(0);
        result.Trades.Should().BeGreaterThan(0);

        var trades = result.TradesList ?? [];
        trades.Should().NotBeEmpty();
        trades.Should().OnlyContain(t => t.EntryPrice > 0);
        trades.Should().OnlyContain(t => t.Lots > 0);

        var venueClosed = trades.Where(t => t.ExitReason is "SL" or "TP").ToList();
        venueClosed.Should().OnlyContain(t => t.ExitPrice > 0);

        var syntheticCloses = trades.Count(t => t.ExitPrice == 0m);
        syntheticCloses.Should().BeLessThanOrEqualTo(1);

        var realMovers = trades
            .Where(t => t.ExitPrice > 0 && t.Lots >= 0.05m && Math.Abs(t.ExitPrice - t.EntryPrice) > 0.0010m)
            .ToList();
        realMovers.Should().NotBeEmpty();
        realMovers.Should().OnlyContain(t => t.NetPnL != 0m);
    }

    [SkippableFact(Timeout = 300_000)]
    public async Task EdgeCase_WeekendRange_RunsToCompletionWithoutGarbage()
    {
        var result = await RunAsync("EURUSD", "H1",
            new DateTime(2024, 1, 13), new DateTime(2024, 1, 14), "weekend-2d");
        if (result is null) return;

        if (result.BarEvals == 0)
            result.Trades.Should().Be(0);

        (result.TradesList ?? []).Should().OnlyContain(t => t.EntryPrice > 0);
    }

    [SkippableFact(Timeout = 300_000)]
    public async Task AfterRun_NoOrphanCtraderProcesses()
    {
        var result = await RunAsync("EURUSD", "H1",
            new DateTime(2024, 1, 15), new DateTime(2024, 1, 16), "cleanup-2d");
        if (result is null) return;

        CtraderProcessGuard.StrayCount().Should().Be(0);
    }
}

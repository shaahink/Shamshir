using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Tests.Simulation.Harness;
using TradingEngine.Tests.Simulation.Verification;

namespace TradingEngine.Tests.Simulation.E2E;

[Trait("Category", "E2E")]
[Trait("Category", "Slow")]
[Trait("RequiresCTrader", "true")]
[Collection("CtraderSerial")]
public sealed class CtraderE2EHarnessSmokeTests
{
    private static bool HasCredentials =>
        !string.IsNullOrEmpty(CtraderTestHelpers.ResolveCredential("CtId", "CTrader__CtId"));

    [Fact(Timeout = 300_000)]
    public async Task EurUsd_H1_3Days_ProducesTrades_UsingPhasedHarness()
    {
        if (!HasCredentials)
        {
            // ⚠ BUG (iter-36, OPEN-ISSUES CT-1): this silent skip reports a misleading PASS while CRITICAL
            // live coverage is NOT running. The fix is the cTrader env (creds + built cBot algo), not the
            // test — see .claude/skills/ctrader-e2e. xUnit v2 has no Assert.Skip; revisit with [SkippableFact].
            Console.WriteLine("[E2E-Smoke] No cTrader credentials — SKIPPING (should run; see ctrader-e2e skill)");
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
        Console.WriteLine($"[{harness.RunId}] Handshake complete");

        await harness.WaitForCompletionAsync(TimeSpan.FromMinutes(4), cts.Token);
        Console.WriteLine($"[{harness.RunId}] Completion reached");

        var result = harness.CollectResult();
        Console.WriteLine($"[{harness.RunId}] Trades={result.Trades} BarEvals={result.BarEvals}");

        result.Trades.Should().BeGreaterThan(0, "3 days EURUSD H1 should produce at least one trade");
        result.FinalTransportStatus.Should().NotBeNull();
    }

    [Fact(Timeout = 300_000)]
    public async Task EurUsd_H1_3Days_ProducesTrades_UsingRunAsync()
    {
        if (!HasCredentials)
        {
            // ⚠ BUG (iter-36, OPEN-ISSUES CT-1): this silent skip reports a misleading PASS while CRITICAL
            // live coverage is NOT running. The fix is the cTrader env (creds + built cBot algo), not the
            // test — see .claude/skills/ctrader-e2e. xUnit v2 has no Assert.Skip; revisit with [SkippableFact].
            Console.WriteLine("[E2E-Smoke] No cTrader credentials — SKIPPING (should run; see ctrader-e2e skill)");
            return;
        }

        var result = await new CtraderE2EHarness("smoke-runasync-3d")
            .WithSymbol("EURUSD", "H1")
            .WithDateRange(new DateTime(2024, 1, 15), new DateTime(2024, 1, 18))
            .RunAsync();

        Console.WriteLine($"[{result.RunId}] Trades={result.Trades} BarEvals={result.BarEvals}");

        result.BarEvals.Should().BeGreaterThan(0, "bars should flow through the pipeline");
        result.Trades.Should().BeGreaterThan(0, "at least one trade expected in 3 days");
    }

    [Fact(Timeout = 300_000)]
    public async Task TradeLedger_ClientOrderIdReconciliation_NoMissingTrades()
    {
        if (!HasCredentials)
        {
            Console.WriteLine("[Ledger-E2E] No cTrader credentials — SKIPPING (should run; see ctrader-e2e skill)");
            return;
        }

        await using var harness = new CtraderE2EHarness("ledger-recon-3d")
            .WithSymbol("EURUSD", "H1")
            .WithDateRange(new DateTime(2024, 1, 15), new DateTime(2024, 1, 18));

        var result = await harness.RunAsync();

        result.Trades.Should().BeGreaterThan(0);
        result.ReportJsonPath.Should().NotBeNull("shamshir-report.json must exist for reconciliation");

        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlite($"Data Source={harness.Artifacts.DbPath}")
            .Options);
        var diff = await CtraderDiffHarness.CompareAsync(db, result.RunId, result.ReportJsonPath!);

        var tradeCountMismatch = diff.Discrepancies
            .FirstOrDefault(d => d.Metric == "TradeCount");
        tradeCountMismatch.Should().BeNull(
            $"trade count must match. cTrader={diff.CtraderTradeCount} DB={diff.DbTradeCount}");

        diff.Discrepancies
            .Where(d => d.Severity == Severity.Error)
            .Should().BeEmpty("no error-severity discrepancies in trade ledger reconciliation");

        Console.WriteLine($"[Ledger-E2E] PASSED — {diff.CtraderTradeCount} trades reconciled");
    }
}

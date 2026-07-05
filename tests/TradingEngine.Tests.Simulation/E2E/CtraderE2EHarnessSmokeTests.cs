using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Tests.Simulation.Harness;
using TradingEngine.Tests.Simulation.Verification;

namespace TradingEngine.Tests.Simulation.E2E;

[Trait("Category", "E2E")]
[Trait("Category", "Slow")]
[Trait("Category", "CtraderContract")]
[Trait("RequiresCTrader", "true")]
[Collection("CtraderSerial")]
public sealed class CtraderE2EHarnessSmokeTests
{
    private static bool HasCredentials =>
        !string.IsNullOrEmpty(CtraderTestHelpers.ResolveCredential("CtId", "CTrader__CtId"));

    [Fact(Skip = "P4.5: retired per cTrader test policy — UsingRunAsync covers the same harness path")]
    public async Task EurUsd_H1_3Days_ProducesTrades_UsingPhasedHarness()
    {
        // iter-38 CT-1: genuinely SKIP when the live cTrader env is absent (was a misleading PASS).
        Skip.IfNot(HasCredentials, "No cTrader credentials — see .claude/skills/ctrader-e2e (CT-1).");

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

    [SkippableFact(Timeout = 300_000)]
    public async Task EurUsd_H1_3Days_ProducesTrades_UsingRunAsync()
    {
        // iter-38 CT-1: genuinely SKIP when the live cTrader env is absent (was a misleading PASS).
        Skip.IfNot(HasCredentials, "No cTrader credentials — see .claude/skills/ctrader-e2e (CT-1).");

        var result = await new CtraderE2EHarness("smoke-runasync-3d")
            .WithSymbol("EURUSD", "H1")
            .WithDateRange(new DateTime(2024, 1, 15), new DateTime(2024, 1, 18))
            .RunAsync();

        Console.WriteLine($"[{result.RunId}] Trades={result.Trades} BarEvals={result.BarEvals}");

        result.BarEvals.Should().BeGreaterThan(0, "bars should flow through the pipeline");
        result.Trades.Should().BeGreaterThan(0, "at least one trade expected in 3 days");
    }

    [SkippableFact(Timeout = 300_000)]
    public async Task TradeLedger_ClientOrderIdReconciliation_NoMissingTrades()
    {
        Skip.IfNot(HasCredentials, "No cTrader credentials — see .claude/skills/ctrader-e2e (CT-1).");

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

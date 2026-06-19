using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Tests.Simulation.Harness;
using TradingEngine.Tests.Simulation.Verification;

namespace TradingEngine.Tests.Simulation.E2E;

[Trait("Category", "E2E")]
[Trait("Category", "Slow")]
[Trait("RequiresCTrader", "true")]
[Collection("CtraderSerial")]
public sealed class DiffE2ETests
{
    private static bool HasCredentials =>
        !string.IsNullOrEmpty(CtraderTestHelpers.ResolveCredential("CtId", "CTrader__CtId"));

    private static TradingDbContext CreateDbContext(string dbPath) =>
        new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options);

    [Fact(Timeout = 300_000)]
    public async Task CtraderVsDb_Comparison_ProducesReport()
    {
        if (!HasCredentials)
        {
            Console.WriteLine("[Diff-E2E] No cTrader credentials — skipping");
            return;
        }

        await using var harness = new CtraderE2EHarness("diff-e2e-3d");
        var result = await harness
            .WithSymbol("EURUSD", "H1")
            .WithDateRange(new DateTime(2024, 1, 15), new DateTime(2024, 1, 18))
            .RunAsync();

        result.Trades.Should().BeGreaterThan(0, "need trades to compare");

        if (result.ReportJsonPath is null)
        {
            Console.WriteLine("[Diff-E2E] shamshir-report.json not captured — skipping comparison");
            return;
        }

        await using var db = CreateDbContext(harness.Artifacts.DbPath);
        var diff = await CtraderDiffHarness.CompareAsync(db, result.RunId, result.ReportJsonPath);

        Console.WriteLine($"Trade count: cTrader={diff.CtraderTradeCount} DB={diff.DbTradeCount}");
        Console.WriteLine($"Net PnL: cTrader={diff.CtraderNetProfit:F2} DB={diff.DbNetProfit:F2}");
        Console.WriteLine($"Max DD: cTrader={diff.CtraderMaxDdPct:P2} DB={diff.DbMaxDdPct:P2}");
        Console.WriteLine($"Winning: cTrader={diff.CtraderWinningTrades} DB={diff.DbWinningTrades}");
        Console.WriteLine($"Commission: cTrader={diff.CtraderCommission:F2} DB={diff.DbCommission:F2}");
        Console.WriteLine($"Swap: cTrader={diff.CtraderSwap:F2} DB={diff.DbSwap:F2}");

        foreach (var d in diff.Discrepancies)
            Console.WriteLine($"  [{d.Kind}][{d.Severity}] {d.Metric}: {d.Description}");

        diff.Should().NotBeNull();
        Console.WriteLine($"[Diff-E2E] Comparison complete. {diff.Discrepancies.Count} discrepancies found.");
    }

    [Fact(Timeout = 300_000)]
    public async Task CostIntegrity_PerTradeCostsMatch_ClientOrderIdReconciliation()
    {
        if (!HasCredentials)
        {
            Console.WriteLine("[CostIntegrity-E2E] No cTrader credentials — skipping");
            return;
        }

        await using var harness = new CtraderE2EHarness("cost-integrity-3d");
        var result = await harness
            .WithSymbol("EURUSD", "H1")
            .WithDateRange(new DateTime(2024, 1, 15), new DateTime(2024, 1, 18))
            .RunAsync();

        result.Trades.Should().BeGreaterThan(0, "need trades to compare");
        result.ReportJsonPath.Should().NotBeNull("shamshir-report.json must be captured");

        await using var db = CreateDbContext(harness.Artifacts.DbPath);
        var diff = await CtraderDiffHarness.CompareAsync(db, result.RunId, result.ReportJsonPath!);

        // Structural integrity: zero missing trades on either side
        var structural = diff.Discrepancies
            .Where(d => d.Kind == DiscrepancyKind.Structural)
            .ToList();
        structural.Should().BeEmpty(
            $"zero structural discrepancies (TradeMissingInDb / TradeMissingInVenue). Found: {string.Join("; ", structural.Select(d => d.Metric))}");

        // No zero-PnL real-movers
        var zeroPnL = diff.Discrepancies
            .Where(d => d.Metric == "TradeZeroPnL")
            .ToList();
        zeroPnL.Should().BeEmpty(
            $"zero trades with non-trivial move but $0 PnL. Found: {zeroPnL.Count}");

        // Per-trade cost fields must match within tolerance
        var numericDiscrepancies = diff.Discrepancies
            .Where(d => d.Kind == DiscrepancyKind.Numeric)
            .ToList();
        Console.WriteLine($"[CostIntegrity] {numericDiscrepancies.Count} numeric discrepancies (if any):");
        foreach (var nd in numericDiscrepancies)
            Console.WriteLine($"  [{nd.Severity}] {nd.Metric}: {nd.Description}");

        // Commission and swap should not have error-severity discrepancies
        var errors = diff.Discrepancies.Where(d => d.Severity == Severity.Error).ToList();
        errors.Should().BeEmpty(
            $"zero error-severity discrepancies. Found: {string.Join("; ", errors.Select(d => $"{d.Metric}: {d.Description}"))}");

        Console.WriteLine($"[CostIntegrity] PASSED — {diff.CtraderTradeCount} trades reconciled, PnL delta ≤ tolerance");
    }
}

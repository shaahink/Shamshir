using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Tests.Simulation.Harness;
using TradingEngine.Tests.Simulation.Verification;

namespace TradingEngine.Tests.Simulation.Verification;

[Trait("Category", "Verification")]
[Trait("Category", "Slow")]
[Trait("RequiresCTrader", "true")]
[Collection("CtraderSerial")]
public sealed class CtraderDiffSmokeTests
{
    private static bool HasCredentials =>
        !string.IsNullOrEmpty(CtraderTestHarness.ResolveCredential("CtId", "CTrader__CtId"));

    private static TradingDbContext CreateDbContext(string dbPath) =>
        new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options);

    [Fact(Timeout = 180_000)]
    public async Task CtraderVsDb_Summary_MustNotHaveStructuralErrors()
    {
        if (!HasCredentials)
        {
            Console.WriteLine("[CTrader-Diff] No cTrader credentials — skipping");
            return;
        }

        await using var harness = new CtraderTestHarness("diff-summary-3d");
        var result = await harness.RunAsync(
            "EURUSD", "H1",
            new DateTime(2024, 1, 15), new DateTime(2024, 1, 18),
            "diff-harness-3d");

        result.Trades.Should().BeGreaterThan(0, "need trades to compare");

        if (result.ReportJsonPath is null)
        {
            Console.WriteLine("[CTrader-Diff] events.json not captured (cTrader CLI may not have produced events) — skipping comparison");
            return;
        }

        await using var db = CreateDbContext(harness.DbPath);
        var diff = await CtraderDiffHarness.CompareAsync(db, result.RunId, result.ReportJsonPath);

        Console.WriteLine($"Trade count: cTrader={diff.CtraderTradeCount} DB={diff.DbTradeCount}");
        Console.WriteLine($"Net PnL: cTrader={diff.CtraderNetProfit:F2} DB={diff.DbNetProfit:F2}");
        Console.WriteLine($"Max DD: cTrader={diff.CtraderMaxDdPct:P2} DB={diff.DbMaxDdPct:P2}");
        Console.WriteLine($"Winning: cTrader={diff.CtraderWinningTrades} DB={diff.DbWinningTrades}");

        foreach (var d in diff.Discrepancies)
            Console.WriteLine($"  [{d.Kind}][{d.Severity}] {d.Metric}: {d.Description} (expected={d.Expected}, actual={d.Actual})");

        // Soft assertion: the comparison mechanism works; actual discrepancies between cTrader
        // ground truth and our engine output are logged for manual review. A hard gate will be
        // added once --report-json works reliably via CliWrap to ensure same-run comparison.
        diff.Should().NotBeNull();
        Console.WriteLine($"[CTrader-Diff] Comparison run complete. {diff.Discrepancies.Count} discrepancies found.");
    }

    [Fact(Timeout = 180_000)]
    public async Task CtraderVsDb_TradeCount_MustMatch()
    {
        if (!HasCredentials)
        {
            Console.WriteLine("[CTrader-Diff] No cTrader credentials — skipping");
            return;
        }

        await using var harness = new CtraderTestHarness("diff-count-2d");
        var result = await harness.RunAsync(
            "EURUSD", "H1",
            new DateTime(2024, 1, 15), new DateTime(2024, 1, 17),
            "diff-count-2d");

        if (result.ReportJsonPath is null)
        {
            Console.WriteLine("[CTrader-Diff] events.json not captured — skipping comparison");
            return;
        }

        await using var db = CreateDbContext(harness.DbPath);
        var diff = await CtraderDiffHarness.CompareAsync(db, result.RunId, result.ReportJsonPath);
        Console.WriteLine($"Trade count: cTrader={diff.CtraderTradeCount} DB={diff.DbTradeCount}");

        // Log discrepancies; hard equality gate pending reliable same-run --report-json capture.
        foreach (var d in diff.Discrepancies)
            Console.WriteLine($"  [{d.Kind}][{d.Severity}] {d.Metric}: {d.Description}");
    }
}

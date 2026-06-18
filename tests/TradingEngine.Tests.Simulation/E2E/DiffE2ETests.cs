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
            Console.WriteLine("[Diff-E2E] events.json not captured — skipping comparison");
            return;
        }

        await using var db = CreateDbContext(harness.Artifacts.DbPath);
        var diff = await CtraderDiffHarness.CompareAsync(db, result.RunId, result.ReportJsonPath);

        Console.WriteLine($"Trade count: cTrader={diff.CtraderTradeCount} DB={diff.DbTradeCount}");
        Console.WriteLine($"Net PnL: cTrader={diff.CtraderNetProfit:F2} DB={diff.DbNetProfit:F2}");
        Console.WriteLine($"Max DD: cTrader={diff.CtraderMaxDdPct:P2} DB={diff.DbMaxDdPct:P2}");
        Console.WriteLine($"Winning: cTrader={diff.CtraderWinningTrades} DB={diff.DbWinningTrades}");

        foreach (var d in diff.Discrepancies)
            Console.WriteLine($"  [{d.Kind}][{d.Severity}] {d.Metric}: {d.Description}");

        diff.Should().NotBeNull();
        Console.WriteLine($"[Diff-E2E] Comparison complete. {diff.Discrepancies.Count} discrepancies found.");
    }
}

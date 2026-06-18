using Microsoft.EntityFrameworkCore;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Tests.Simulation.Harness;
using TradingEngine.Tests.Simulation.Verification;

namespace TradingEngine.Tests.Simulation.E2E;

/// <summary>
/// Discovery-phase: run a 1-month EURUSD H1 backtest with a single strategy (mean-reversion)
/// against real cTrader and audit the discrepancies. No hard assertions on diffs — this is
/// an information-producing test, not a gate. Review the output to cluster findings, then
/// write targeted tests per discrepancy category.
/// </summary>
[Trait("Category", "Discovery")]
[Trait("Category", "Slow")]
[Trait("RequiresCTrader", "true")]
[Collection("CtraderSerial")]
public sealed class DiscoveryAuditTests
{
    private static bool HasCredentials =>
        !string.IsNullOrEmpty(CtraderTestHelpers.ResolveCredential("CtId", "CTrader__CtId"));

    private static TradingDbContext CreateDbContext(string dbPath) =>
        new(new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options);

    [Fact(Timeout = 600_000)]
    public async Task EurUsd_H1_1Month_MeanReversion_FullAudit()
    {
        if (!HasCredentials)
        {
            Console.WriteLine("[Discovery] No cTrader credentials — skipping");
            return;
        }

        var testName = "discovery-eurusd-h1-1m-meanrev";
        await using var harness = new CtraderE2EHarness(testName)
            .WithSymbol("EURUSD", "H1")
            .WithStrategyIds("mean-reversion")
            .WithDateRange(new DateTime(2024, 1, 1), new DateTime(2024, 2, 1));

        var result = await harness.RunAsync();

        await using var db = CreateDbContext(harness.Artifacts.DbPath);

        var reportPath = result.ReportJsonPath
            ?? harness.Artifacts.EventsJsonPath;

        Console.WriteLine($"=== DISCOVERY AUDIT ===");
        Console.WriteLine($"RunId:         {result.RunId}");
        Console.WriteLine($"Symbol:        EURUSD H1");
        Console.WriteLine($"Period:        2024-01-01 → 2024-02-01");
        Console.WriteLine($"Strategy:      mean-reversion (only)");
        Console.WriteLine($"DB path:       {harness.Artifacts.DbPath}");
        Console.WriteLine($"Report path:   {reportPath ?? "NOT CAPTURED"}");
        Console.WriteLine($"Transport:     {result.FinalTransportStatus?.Phase ?? "unknown"}");
        Console.WriteLine($"CLI exit:      {result.CliExitCode}");
        Console.WriteLine($"CLI stderr:    {(string.IsNullOrEmpty(result.CliStderr) ? "(empty)" : result.CliStderr[..Math.Min(200, result.CliStderr.Length)])}");
        Console.WriteLine();

        if (reportPath is null)
        {
            Console.WriteLine("[Discovery] No cTrader report captured — cannot diff. Check:");
            Console.WriteLine($"  ReportJsonPath:  {harness.Artifacts.ReportJsonPath} (exists={File.Exists(harness.Artifacts.ReportJsonPath)})");
            Console.WriteLine($"  EventsJsonPath:  {harness.Artifacts.EventsJsonPath} (exists={File.Exists(harness.Artifacts.EventsJsonPath)})");
            return;
        }

        var diff = await CtraderDiffHarness.CompareAsync(db, result.RunId, reportPath);

        Console.WriteLine("── Summary Comparison ──");
        Console.WriteLine($"  Trade count:     cTrader={diff.CtraderTradeCount,5}  DB={diff.DbTradeCount,5}  {(diff.CtraderTradeCount == diff.DbTradeCount ? "✓" : "✗ MISMATCH")}");
        Console.WriteLine($"  Net PnL:         cTrader={diff.CtraderNetProfit,10:F2}  DB={diff.DbNetProfit,10:F2}  Δ={Math.Abs(diff.CtraderNetProfit - diff.DbNetProfit):F2}");
        Console.WriteLine($"  Max DD%:         cTrader={diff.CtraderMaxDdPct,8:P2}  DB={diff.DbMaxDdPct,8:P2}");
        Console.WriteLine($"  Winning trades:  cTrader={diff.CtraderWinningTrades,5}  DB={diff.DbWinningTrades,5}");
        Console.WriteLine($"  Commission:      cTrader={diff.CtraderCommission,10:F2}  DB={diff.DbCommission,10:F2}");
        Console.WriteLine($"  Swap:            cTrader={diff.CtraderSwap,10:F2}  DB={diff.DbSwap,10:F2}");

        Console.WriteLine();
        Console.WriteLine($"── Discrepancies ({diff.Discrepancies.Count}) ──");

        var structural = diff.Discrepancies.Where(d => d.Kind == DiscrepancyKind.Structural).ToList();
        var numeric = diff.Discrepancies.Where(d => d.Kind == DiscrepancyKind.Numeric).ToList();

        if (structural.Count > 0)
        {
            Console.WriteLine($"  Structural ({structural.Count}):");
            foreach (var d in structural)
                Console.WriteLine($"    [{d.Severity}] {d.Metric}: {d.Description}");
        }

        if (numeric.Count > 0)
        {
            Console.WriteLine($"  Numeric ({numeric.Count}):");
            foreach (var d in numeric)
                Console.WriteLine($"    [{d.Severity}] {d.Metric}: {d.Description}");
        }

        if (diff.IsClean)
            Console.WriteLine("  No discrepancies found — cTrader and DB agree.");

        Console.WriteLine();
        Console.WriteLine("── DB Trade Detail ──");
        if (result.TradesList is { Count: > 0 } trades)
        {
            Console.WriteLine(string.Format("  {0,-8} {1,-6} {2,6} {3,8} {4,8} {5,10} {6,8} {7,8} {8,10} {9,-10} {10,-16}",
                "Sym", "Dir", "Lots", "Entry", "Exit", "Gross", "Comm", "Swap", "Net", "Reason", "Strat"));
            foreach (var t in trades)
                Console.WriteLine($"  {t.Symbol,-8} {t.Direction,-6} {t.Lots,6:F2} {t.EntryPrice,8:F5} {t.ExitPrice,8:F5} {t.GrossPnL,10:F2} {t.Commission,8:F2} {t.Swap,8:F2} {t.NetPnL,10:F2} {t.ExitReason,-10} {t.StrategyId,-16}");
        }
        else
        {
            Console.WriteLine("  No trades in DB.");
        }

        Console.WriteLine();
        Console.WriteLine($"=== AUDIT COMPLETE ===");
    }
}

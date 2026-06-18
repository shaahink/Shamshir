using TradingEngine.Tests.Simulation.Harness;

namespace TradingEngine.Tests.Simulation.Pipeline;

/// <summary>
/// Real end-to-end scenarios: engine ↔ NetMQ ↔ the actual cBot in cTrader's backtester over live
/// market data. These assert invariants on the resulting trade ledger (not just "bars flowed"), so
/// they catch data-integrity regressions — garbage fill prices, missing exit reasons, orphaned
/// processes — that only surface on a real run. Skipped automatically when no cTrader credentials
/// are configured (CI without creds). Slow (~25-30s each: auth + data download + backtest).
/// </summary>
[Trait("Category", "Pipeline")]
[Trait("Category", "Slow")]
[Trait("RequiresCTrader", "true")]
[Collection("CtraderSerial")]
public sealed class CtraderScenarioTests
{
    private static bool HasCredentials =>
        !string.IsNullOrEmpty(CtraderTestHarness.ResolveCredential("CtId", "CTrader__CtId"));

    private static async Task<CtraderTestHarness.Result?> RunAsync(
        string symbol, string period, DateTime start, DateTime end, string label)
    {
        if (!HasCredentials)
        {
            Console.WriteLine($"[{label}] No cTrader credentials — skipping");
            return null;
        }
        await using var harness = new CtraderTestHarness(label);
        return await harness.RunAsync(symbol, period, start, end, label);
    }

    [Fact(Timeout = 180_000)]
    public async Task HappyPath_EurUsd_TradeLedgerHasIntegrity()
    {
        var result = await RunAsync("EURUSD", "H1",
            new DateTime(2024, 1, 15), new DateTime(2024, 1, 18), "EURUSD-H1-3D-integrity");
        if (result is null) return;

        result.BarEvals.Should().BeGreaterThan(0, "bars must flow through the full pipeline");
        result.Signals.Should().BeGreaterThan(0, "strategies should fire over 3 days");
        result.Trades.Should().BeGreaterThan(0, "at least one trade should close");

        var trades = result.TradeRows ?? [];
        trades.Should().NotBeEmpty();

        // Entry always fills at a real venue price — a zero/negative entry is a garbage fill.
        trades.Should().OnlyContain(t => t.EntryPrice > 0,
            "every trade must have a real entry price (no Price(0) garbage fills)");

        // Lots are always positive and within a sane band for a $100k account.
        trades.Should().OnlyContain(t => t.Lots > 0, "lots must be positive");

        // A trade closed by the venue on SL/TP must have a real exit price. End-of-run positions
        // flattened by the engine's synthetic close (ExitReason FORCE/SHUTDOWN) legitimately carry
        // ExitPrice 0 / PnL 0 (M2) — exclude those, but assert there is at most one per run.
        var venueClosed = trades.Where(t => t.ExitReason is "SL" or "TP").ToList();
        venueClosed.Should().OnlyContain(t => t.ExitPrice > 0,
            "SL/TP closes must have a real venue exit price");

        var syntheticCloses = trades.Count(t => t.ExitPrice == 0m);
        syntheticCloses.Should().BeLessThanOrEqualTo(1,
            "only the single end-of-run open position should be flattened by a synthetic zero-price close");

        // PnL integrity: a closed trade with a real exit price and a non-trivial price move MUST
        // carry non-zero PnL. This previously failed — engine-requested closes (close_position)
        // recorded $0 PnL because the cBot hard-coded grossProfit/netProfit=0 in MakeExecResult
        // (real PnL only came through cTrader's own async closes). A 96-pip adverse move was
        // booked as $0.00. Fixed by capturing the realized PnL before ClosePosition.
        var realMovers = trades
            .Where(t => t.ExitPrice > 0 && t.Lots >= 0.05m && Math.Abs(t.ExitPrice - t.EntryPrice) > 0.0010m)
            .ToList();
        realMovers.Should().NotBeEmpty("the 3-day window should contain trades with real price movement");
        realMovers.Should().OnlyContain(t => t.NetPnL != 0m,
            "a trade with a >10-pip move on >=0.05 lots cannot have exactly $0 PnL — that is the " +
            "hard-coded-zero-PnL bug in the cBot's command-close path");
    }

    [Fact(Timeout = 180_000)]
    public async Task EdgeCase_WeekendRange_RunsToCompletionWithoutGarbage()
    {
        // Sat→Sun: forex is largely closed, so very few/no bars. The pipeline must complete cleanly
        // and never fabricate trades from no data.
        var result = await RunAsync("EURUSD", "H1",
            new DateTime(2024, 1, 13), new DateTime(2024, 1, 14), "EURUSD-weekend");
        if (result is null) return;

        result.BarEvals.Should().BeGreaterThanOrEqualTo(0);
        if (result.BarEvals == 0)
            result.Trades.Should().Be(0, "no bars means no trades — the engine must not invent any");

        (result.TradeRows ?? []).Should().OnlyContain(t => t.EntryPrice > 0,
            "any trade produced must still have a valid entry price");
    }

    [Fact(Timeout = 180_000)]
    public async Task AfterRun_NoOrphanCtraderProcesses()
    {
        var result = await RunAsync("EURUSD", "H1",
            new DateTime(2024, 1, 15), new DateTime(2024, 1, 16), "EURUSD-cleanup");
        if (result is null) return;

        // The CLI process (and any child ctrader-cli) must be reaped — no leaked backtester
        // processes after the harness disposes.
        CtraderProcessGuard.StrayCount().Should().Be(0,
            "the cTrader CLI must be fully torn down after a run (no orphans)");
    }
}

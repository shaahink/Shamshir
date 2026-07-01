using FluentAssertions;
using TradingEngine.Infrastructure.Reconcile;

namespace TradingEngine.Tests.Unit.Reconcile;

/// <summary>iter-marketdata-tape P0 — the reconciler classifies divergences (RawMoney vs Aggregation vs
/// TradeSet) and honours per-category tolerances.</summary>
public sealed class LedgerReconcilerTests
{
    private static ReconcileLedger Ledger(
        decimal net = 100m, decimal gross = 110m, decimal comm = 6m, decimal swap = 4m,
        double maxDd = 5.0, int total = 10, int winning = 6, double winRate = 60.0) =>
        new("x", net, gross, comm, swap, maxDd, total, winning, winRate, Array.Empty<ReconcileTrade>());

    [Fact]
    public void Identical_ledgers_match()
    {
        LedgerReconciler.Compare(Ledger(), Ledger()).IsMatch.Should().BeTrue();
    }

    [Fact]
    public void Money_within_tolerance_does_not_diverge()
    {
        var report = LedgerReconciler.Compare(Ledger(net: 100.00m), Ledger(net: 100.005m));
        report.IsMatch.Should().BeTrue("0.005 < default 0.01 money tolerance");
    }

    [Fact]
    public void Net_profit_gap_is_flagged_as_RawMoney()
    {
        var report = LedgerReconciler.Compare(Ledger(net: 100m), Ledger(net: 130m));
        var div = report.ByCategory(DivergenceCategory.RawMoney).Should().ContainSingle().Subject;
        div.Field.Should().Be("NetProfit");
        div.AbsDiff.Should().BeApproximately(30, 1e-9);
    }

    [Fact]
    public void MaxDrawdown_gap_is_flagged_as_Aggregation()
    {
        // The canonical known divergence: engine DB MaxDD 0 vs venue 4.6% (engine can't see floating DD).
        var report = LedgerReconciler.Compare(Ledger(maxDd: 0.0), Ledger(maxDd: 4.6));
        report.ByCategory(DivergenceCategory.Aggregation).Should().ContainSingle(d => d.Field == "MaxDrawdownPct");
        report.ByCategory(DivergenceCategory.RawMoney).Should().BeEmpty();
    }

    [Fact]
    public void Trade_count_gap_is_flagged_as_TradeSet()
    {
        var report = LedgerReconciler.Compare(Ledger(total: 9), Ledger(total: 10));
        report.ByCategory(DivergenceCategory.TradeSet).Should().ContainSingle(d => d.Field == "TotalTrades");
    }
}

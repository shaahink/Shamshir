namespace TradingEngine.Infrastructure.Reconcile;

/// <summary>
/// iter-marketdata-tape P0 — diffs an engine ledger against a venue (cTrader) ledger, classifying each
/// divergence so the owner can tell "real bug" (RawMoney) from "expected re-aggregation gap"
/// (Aggregation/TradeSet). Pure: no I/O, deterministic, fully unit-tested.
/// </summary>
public static class LedgerReconciler
{
    public static ReconcileReport Compare(ReconcileLedger engine, ReconcileLedger venue, ReconcileTolerance? tolerance = null)
    {
        var tol = tolerance ?? new ReconcileTolerance();
        var d = new List<Divergence>();

        void Money(string field, decimal e, decimal v, DivergenceCategory cat)
        {
            if (Math.Abs(e - v) > tol.Money) d.Add(new Divergence(field, cat, (double)e, (double)v));
        }
        void Pct(string field, double e, double v)
        {
            if (Math.Abs(e - v) > tol.Pct) d.Add(new Divergence(field, DivergenceCategory.Aggregation, e, v));
        }
        void Count(string field, int e, int v)
        {
            if (Math.Abs(e - v) > tol.TradeCount) d.Add(new Divergence(field, DivergenceCategory.TradeSet, e, v));
        }

        // RawMoney — should agree to the cent (the cBot forwards cTrader's own economics).
        Money("NetProfit", engine.NetProfit, venue.NetProfit, DivergenceCategory.RawMoney);
        Money("GrossProfit", engine.GrossProfit, venue.GrossProfit, DivergenceCategory.RawMoney);
        Money("Commission", engine.Commission, venue.Commission, DivergenceCategory.RawMoney);
        Money("Swap", engine.Swap, venue.Swap, DivergenceCategory.RawMoney);

        // TradeSet — counts.
        Count("TotalTrades", engine.TotalTrades, venue.TotalTrades);
        Count("WinningTrades", engine.WinningTrades, venue.WinningTrades);

        // Aggregation — the expected-divergence bucket.
        Pct("MaxDrawdownPct", engine.MaxDrawdownPct, venue.MaxDrawdownPct);
        Pct("WinRatePct", engine.WinRatePct, venue.WinRatePct);

        // Per-trade deltas — match by (OpenedAtUtc, Direction, Lots) and compute cost differences.
        var tradeDeltas = ComputeTradeDeltas(engine.Trades, venue.Trades);

        return new ReconcileReport(d, tradeDeltas);
    }

    private static IReadOnlyList<PerTradeDelta> ComputeTradeDeltas(
        IReadOnlyList<ReconcileTrade> leftTrades, IReadOnlyList<ReconcileTrade> rightTrades)
    {
        var deltas = new List<PerTradeDelta>();
        var rightCopy = rightTrades.ToList();

        foreach (var lt in leftTrades)
        {
            var matchIdx = rightCopy.FindIndex(rt =>
                Math.Abs((rt.OpenedAtUtc - lt.OpenedAtUtc).TotalMinutes) < 5 &&
                rt.Direction == lt.Direction &&
                Math.Abs(rt.Lots - lt.Lots) < 0.001m);

            if (matchIdx < 0) continue;

            var rt = rightCopy[matchIdx];
            rightCopy.RemoveAt(matchIdx);

            deltas.Add(new PerTradeDelta(
                lt.OpenedAtUtc, lt.Direction, lt.Lots,
                lt.EntryPrice, lt.ExitPrice,
                CommissionDelta: lt.Commission - rt.Commission,
                SwapDelta: lt.Swap - rt.Swap,
                NetDelta: lt.NetPnL - rt.NetPnL));
        }

        return deltas;
    }
}

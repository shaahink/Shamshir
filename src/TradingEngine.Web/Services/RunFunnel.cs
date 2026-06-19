namespace TradingEngine.Web.Services;

/// <summary>
/// Per-strategy decision funnel (signals → orders → fills → closes) computed from a run's decision
/// timeline. Rehomed from the scrapped Razor <c>ReportModel</c> during the Angular migration so the
/// logic (and its iter-27 regression test) survives, and so a future <c>/api/runs/{id}/funnel</c>
/// endpoint can reuse it (Track C — A3 per-strategy funnel).
/// </summary>
public static class RunFunnel
{
    public sealed record FunnelRow(string StrategyId, int Signals, int Orders, int Fills, int Closes, double WinRate);

    /// <summary>
    /// "OrderSubmitted" is written by BOTH the OrderDispatcher and the lifecycle FSM, so we count only
    /// the lifecycle one (Reason=="Accepted") to avoid double-counting. BAR_EVAL is per-bar noise, not a
    /// signal, so Signals = orders+rejects (intents that reached the risk gate) — NOT the bar count.
    /// Close reasons cover SL/TP/forced exits.
    /// </summary>
    public static List<FunnelRow> BuildFunnel(IEnumerable<DecisionRecordView> timeline)
    {
        static bool IsClose(string? r) => r is "SL" or "TP" or "FORCE" or "DailyDD" or "MaxDD";
        return timeline
            .GroupBy(d => d.StrategyId ?? "unknown")
            .Select(g =>
            {
                var orders = g.Count(d => d.Event == "OrderSubmitted" && d.Reason == "Accepted");
                var rejects = g.Count(d => d.Event == "OrderRejected");
                var fills = g.Count(d => d.Event == "OrderFilled" && (d.Reason == "Filled" || d.Reason == "PartialFill"));
                var closes = g.Count(d => d.Event == "OrderFilled" && IsClose(d.Reason));
                var tpCloses = g.Count(d => d.Event == "OrderFilled" && d.Reason == "TP");
                return new FunnelRow(
                    g.Key,
                    Signals: orders + rejects,
                    Orders: orders,
                    Fills: fills,
                    Closes: closes,
                    WinRate: closes > 0 ? (double)tpCloses / closes * 100 : 0);
            }).ToList();
    }
}

namespace TradingEngine.Infrastructure.Reconcile;

/// <summary>
/// iter-marketdata-tape P0 — a source-agnostic, normalized ledger used to reconcile our engine's DB result
/// against cTrader's own recorded result (the oracle). Both sides map onto this shape; the reconciler
/// diffs two of them. Kept free of Domain/EF types so it stays pure and trivially testable.
/// </summary>
public sealed record ReconcileLedger(
    string Source,
    decimal NetProfit,
    decimal GrossProfit,
    decimal Commission,
    decimal Swap,
    double MaxDrawdownPct,
    int TotalTrades,
    int WinningTrades,
    double WinRatePct,
    IReadOnlyList<ReconcileTrade> Trades);

public sealed record ReconcileTrade(
    DateTime OpenedAtUtc,
    DateTime ClosedAtUtc,
    string Direction,
    decimal Lots,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal Commission,
    decimal Swap,
    decimal NetPnL,
    string? ExitReason);

/// <summary>
/// Why a field diverges. RawMoney (net/gross/commission/swap) SHOULD be ~0 — on the cTrader path the cBot
/// forwards cTrader's own economics, so a RawMoney divergence means a real bug. Aggregation
/// (MaxDD/WinRate/equity) is where divergence is EXPECTED (the engine re-derives these from sparser data —
/// e.g. it can't see intrabar floating drawdown). TradeSet = counts (late-settlement undercounts here).
/// </summary>
public enum DivergenceCategory { RawMoney, Aggregation, TradeSet }

public sealed record Divergence(string Field, DivergenceCategory Category, double EngineValue, double VenueValue)
{
    public double AbsDiff => Math.Abs(EngineValue - VenueValue);
    public double RelDiff => VenueValue != 0 ? AbsDiff / Math.Abs(VenueValue) : (EngineValue == 0 ? 0 : 1);
}

/// <summary>Per-category tolerances. Money is an absolute currency amount; Pct is absolute percentage points.</summary>
public sealed record ReconcileTolerance(decimal Money = 0.01m, double Pct = 0.05, int TradeCount = 0);

public sealed record ReconcileReport(IReadOnlyList<Divergence> Divergences, IReadOnlyList<PerTradeDelta> TradeDeltas)
{
    public bool IsMatch => Divergences.Count == 0;

    public IEnumerable<Divergence> ByCategory(DivergenceCategory category) =>
        Divergences.Where(d => d.Category == category);

    public string ToText()
    {
        if (IsMatch && TradeDeltas.Count == 0) return "RECONCILE: MATCH (no divergences beyond tolerance)";
        var lines = new List<string>();
        if (Divergences.Count > 0)
        {
            lines.Add("RECONCILE: DIVERGENCES");
            foreach (var d in Divergences.OrderBy(d => d.Category))
                lines.Add($"  [{d.Category,-11}] {d.Field,-16} engine={d.EngineValue,12:F4}  venue={d.VenueValue,12:F4}  Δ={d.AbsDiff,10:F4}");
        }
        if (TradeDeltas.Count > 0)
        {
            lines.Add("PER-TRADE DELTAS:");
            lines.Add("  #  openedAt            dir  lots    entry     exit      commission  swap       net");
            for (var i = 0; i < TradeDeltas.Count; i++)
            {
                var td = TradeDeltas[i];
                lines.Add($"  {i+1,2}  {td.OpenedAtUtc:yyyy-MM-dd HH:mm}  {td.Direction,4}  {td.Lots,5:F2}  {td.EntryPrice,8:F5}  {td.ExitPrice,8:F5}  {td.CommissionDelta,10:F2}  {td.SwapDelta,8:F2}  {td.NetDelta,8:F2}");
            }
        }
        return string.Join("\n", lines);
    }
}

/// <summary>Per-trade comparison between two venue legs. Costs use the unified negative convention (D9).</summary>
public sealed record PerTradeDelta(
    DateTime OpenedAtUtc,
    string Direction,
    decimal Lots,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal CommissionDelta,
    decimal SwapDelta,
    decimal NetDelta);

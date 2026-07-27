namespace TradingEngine.Services.Helpers;

/// <summary>
/// F87 P4: the ONE place that decides which spread <b>number</b> a consumer reads — before this,
/// three independent sources were live in a single run (fills via the override chain, floating PnL
/// via a direct <c>TypicalSpread</c> read, synthesized strategy ticks via per-site
/// <c>TypicalSpread / 2</c> helpers), so the same bar could be priced off three different spreads.
///
/// <para>Resolution order: the bar's RECORDED per-bar spread when present (Dukascopy import carries
/// one on every bar) → the registry's <c>TypicalSpread</c> → the caller's historical fallback (each
/// call site keeps the fallback it always had). A recorded 0 is a real value, not absence.</para>
///
/// <para>R3: offset conventions stay at the call sites — full-spread ask on fills and floating PnL,
/// half-spread on synthesized close ticks. This class resolves only the number that feeds them.
/// The run-level explicit override (F32 parity contract) also stays with its owner
/// (<c>TapeReplayAdapter.GetSpread</c>), which consults this resolver when no override is set.</para>
/// </summary>
public static class SpreadResolver
{
    /// <summary>Full spread in price units (ask − bid).</summary>
    public static decimal FullSpread(
        decimal? recordedSpread, ISymbolInfoRegistry registry, Symbol symbol, decimal fallback)
    {
        if (recordedSpread is { } s) return s;
        try { return registry.Get(symbol).TypicalSpread; }
        catch { return fallback; }
    }
}

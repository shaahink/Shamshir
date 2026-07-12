namespace TradingEngine.Infrastructure.Adapters;

/// <summary>
/// P4.3 (F43): the venue's resting-order fill model, shared by both replay venues so the two can never
/// drift apart (the same reason <see cref="SpreadConvention"/> exists).
///
/// cTrader's backtest replays each M1 bar as FOUR synthetic ticks — Open, High, Low, Close. There is no
/// tick at an arbitrary price. A resting order therefore triggers on the first of those four ticks to
/// breach its level and fills AT THAT TICK — never at the order's own price. When the level sits strictly
/// between the bar's open and the breaching extreme, the fill lands ON THE EXTREME.
///
/// So a stop fills THROUGH the stop, and a limit fills BETTER than the limit. Both venues previously
/// modelled every resting order as "fill at the level", which is why tape long-stops were ~5 pips
/// optimistic, tape limits ~0.2 pips pessimistic, and tape short-stops a full spread pessimistic (the
/// spread was also being counted twice — the bar was already shifted to the ask side for detection).
///
/// Verified tick-exact against cTrader on six independent fills — EURUSD and XAUUSD, both directions,
/// limit entry + stop-loss + take-profit. See docs/iterations/iter-alpha-loop/PARITY-TRUTH-4.md §2.
///
/// NOTE this reproduces the venue's M1 tick synthesis, artifacts included. It is a faithful model of the
/// cTrader *backtest*, not of live execution, where a limit fills at the limit and a stop fills near it.
/// Closing that gap needs tick-resolution data on both legs — see PARITY-TRUTH-4.md §5.
/// </summary>
internal static class VenueFillModel
{
    /// <summary>
    /// The price at which the venue fills a resting order on the bar that breaches it.
    /// </summary>
    /// <param name="sideBar">The bar ALREADY shifted to the order's own side of the book: the raw bid bar
    /// for anything that executes at the bid (long exit, short entry), the ask bar for anything that
    /// executes at the ask (long entry, short exit). See <see cref="SpreadConvention.AskBar"/>.</param>
    /// <param name="level">The resting order's own price (limit, stop, stop-loss or take-profit).</param>
    /// <param name="fallsToLevel">True when price must FALL to reach the level (long SL, short TP, buy
    /// limit, sell stop); false when it must RISE to reach it (short SL, long TP, sell limit, buy stop).</param>
    public static decimal FirstBreachingTick(Bar sideBar, decimal level, bool fallsToLevel)
        => fallsToLevel
            // The open tick already breached (price gapped past the level before the bar began) → the
            // fill is the open. Otherwise the low is the first of O/H/L/C to reach down through it.
            ? (sideBar.Open <= level ? sideBar.Open : sideBar.Low)
            : (sideBar.Open >= level ? sideBar.Open : sideBar.High);
}

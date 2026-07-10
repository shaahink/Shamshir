namespace TradingEngine.Infrastructure.Adapters;

/// <summary>
/// P0.2 (D3): the single bid-bar spread convention shared by both replay venues
/// (<see cref="TapeReplayAdapter"/>, <see cref="BacktestReplayAdapter"/>). Recorded bars are BID;
/// ask(t) = bid(t) + spread(t). Longs buy@ask / sell@bid; shorts sell@bid / buy@ask.
///
/// Centralized here so the shift applied when DETECTING a short's SL/TP hit and the adjustment applied
/// to the resulting FILL PRICE can never drift apart again — that exact drift (one path using a stale
/// half-spread constant while the other moved on) is how the pre-P0.2 half-spread bug happened, twice,
/// independently, in both adapters.
/// </summary>
internal static class SpreadConvention
{
    public static decimal AskPrice(decimal bidPrice, decimal spread) => bidPrice + spread;

    /// <summary>Shifts every OHLC field of a bid bar to the ask side, for SL/TP detection on short
    /// positions (a short's stop/target is crossed by the ASK, not the raw/bid bar).</summary>
    public static Bar AskBar(Bar bidBar, decimal spread) => new(
        bidBar.Symbol, bidBar.Timeframe, bidBar.OpenTimeUtc,
        bidBar.Open + spread, bidBar.High + spread, bidBar.Low + spread, bidBar.Close + spread, bidBar.Volume);
}

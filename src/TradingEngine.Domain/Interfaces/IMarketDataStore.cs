namespace TradingEngine.Domain;

/// <summary>
/// Canonical, source-agnostic market-data store (iter-marketdata-tape P1). Holds OHLCV history keyed by
/// (symbol, timeframe, openTime) — NOT per-RunId like the run's <c>Bars</c> table — so downloaded history is
/// deduplicated and reused across any number of backtests. Reads are plain range scans; the fast in-process
/// fake venue (TapeReplayAdapter) and the reconciliation/experiment paths read through this. Ticks are a
/// later phase (P6) and deliberately not modelled here yet.
/// </summary>
public interface IMarketDataStore
{
    /// <summary>Insert bars, deduping on (symbol, timeframe, openTime). Returns the number actually inserted
    /// (idempotent: re-writing the same window inserts 0). <paramref name="source"/> tags provenance
    /// (e.g. "ctrader", "dukascopy") so mixed feeds stay distinguishable.</summary>
    Task<int> WriteBarsAsync(string source, IReadOnlyList<Bar> bars, CancellationToken ct = default,
        IProgress<int>? progress = null);

    /// <summary>Ordered (ascending openTime) bars for a symbol/timeframe in [fromUtc, toUtc].</summary>
    Task<IReadOnlyList<Bar>> ReadBarsAsync(Symbol symbol, Timeframe tf, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    Task<int> CountBarsAsync(Symbol symbol, Timeframe tf, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>What data we hold: one row per (symbol, timeframe, source) with its coverage + count.
    /// This is the UI's "no guesswork" inventory (owner requirement).</summary>
    Task<IReadOnlyList<MarketDataInventoryEntry>> GetInventoryAsync(CancellationToken ct = default);

    /// <summary>Missing-bar gaps in stored coverage for a symbol/timeframe over [fromUtc, toUtc], with a
    /// weekend-straddle flag so callers can distinguish real holes from normal FX weekend closes.</summary>
    Task<IReadOnlyList<MarketDataGap>> GetGapsAsync(Symbol symbol, Timeframe tf, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Delete stored bars for a (symbol, timeframe). Null <paramref name="fromUtc"/>/
    /// <paramref name="toUtc"/> = whole range; null <paramref name="source"/> = all sources.
    /// Only ever touches downloaded market-data history — never a run's per-RunId Bars.</summary>
    Task<int> DeleteBarsAsync(Symbol symbol, Timeframe tf, DateTime? fromUtc, DateTime? toUtc, string? source, CancellationToken ct = default);
}

public sealed record MarketDataInventoryEntry(
    string Symbol,
    Timeframe Timeframe,
    string Source,
    DateTime FirstOpenUtc,
    DateTime LastOpenUtc,
    long BarCount);

/// <param name="AfterOpenUtc">Open time of the last bar before the gap.</param>
/// <param name="NextOpenUtc">Open time of the first bar after the gap.</param>
/// <param name="MissingBars">How many timeframe-intervals are absent between them.</param>
/// <param name="StraddlesWeekend">True if the gap spans a Saturday (likely a normal FX weekend, not a hole).</param>
public sealed record MarketDataGap(
    DateTime AfterOpenUtc,
    DateTime NextOpenUtc,
    int MissingBars,
    bool StraddlesWeekend);

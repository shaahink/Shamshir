namespace TradingEngine.Infrastructure.MarketData.Sync;

/// <summary>
/// X4 — one watchlist row: a symbol × timeframe the auto-sync service keeps filled from
/// <see cref="BackfillFromUtc"/> up to "now". This is the ONLY persisted sync state; the actual work
/// each tick is derived from live DB coverage (the durable source of truth), so a restart mid-sync just
/// recomputes the still-missing range and re-fills it (ingest is idempotent). No stuck-job states.
/// Stored in <c>marketdata.db</c> alongside the bars it governs.
/// </summary>
public sealed class MarketDataSyncCell
{
    /// <summary>Symbol, upper-case (e.g. <c>EURUSD</c>).</summary>
    public string Symbol { get; set; } = "";

    /// <summary>Timeframe token, lower-case (e.g. <c>h1</c>, <c>m1</c>).</summary>
    public string Timeframe { get; set; } = "";

    /// <summary>The floor the cell is backfilled to. Owner default: 2020-01-01.</summary>
    public DateTime BackfillFromUtc { get; set; } = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>When false the auto-sync service skips this cell (kept for one-click re-enable).</summary>
    public bool Enabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

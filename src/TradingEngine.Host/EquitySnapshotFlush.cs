using TradingEngine.Domain;

namespace TradingEngine.Host;

/// <summary>
/// iter-37 K-GAP-2: persists backtest equity. Per bar, <see cref="EngineRunner.ReportBar"/> records an
/// <see cref="AccountSnapshot"/> into the in-memory <c>BufferedEquitySink</c> (so the live Monitor can read
/// it cheaply during the run). Before the cutover those buffered snapshots were dropped when the inner host
/// was disposed, so <c>GET /api/runs/{id}/equity</c> (which reads the <c>EquitySnapshots</c> table) was
/// empty for a finished backtest. This flushes the buffer to the table in ONE batched write at completion —
/// the preferred fix over per-bar DB writes (keeps the per-bar live read cheap, no per-bar I/O).
///
/// Also the single mapping <see cref="ToEquity"/> from the authoritative <see cref="AccountSnapshot"/> to the
/// persisted <see cref="EquitySnapshot"/>, carrying the run's real <see cref="EngineMode"/> (the old
/// <c>PersistentEquitySink</c> hard-coded <c>Live</c>).
/// </summary>
public static class EquitySnapshotFlush
{
    public static EquitySnapshot ToEquity(AccountSnapshot s, EngineMode mode) => new(
        TimestampUtc: s.SimTimeUtc,
        Balance: s.Balance,
        FloatingPnL: s.FloatingPnL,
        Equity: s.Equity,
        PeakEquity: s.PeakEquity,
        DailyStartEquity: s.DailyStartEquity,
        CurrentDailyDrawdown: s.DailyDrawdown,
        CurrentMaxDrawdown: s.MaxDrawdown,
        Mode: mode);

    public static async Task FlushAsync(
        IReadOnlyList<AccountSnapshot> snapshots, IEquityRepository repo,
        EngineMode mode, string runId, CancellationToken ct)
    {
        if (snapshots.Count == 0) return;
        var mapped = new List<EquitySnapshot>(snapshots.Count);
        for (var i = 0; i < snapshots.Count; i++)
            mapped.Add(ToEquity(snapshots[i], mode));
        await repo.SaveBatchAsync(mapped, runId, ct);
    }
}

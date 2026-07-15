using System.Collections.Concurrent;
using TradingEngine.CTraderRunner;
using TradingEngine.Infrastructure.Adapters;

namespace TradingEngine.Web.Services;

// P0.2 (F5, Q5): a teardown/persistence anomaly that does NOT invalidate a complete engine result.
public sealed record RunWarning(string Code, string Detail, DateTime AtUtc);

/// <summary>The orchestrator's in-memory state for one run — everything a live GET or SignalR
/// frame reads while the run is in flight. Owned and mutated by <see cref="BacktestOrchestrator"/>
/// and the venue runners; read by the query side (<see cref="RunQueryService"/>).</summary>
public sealed record BacktestRunState
{
    public required string RunId { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public string Status { get; set; } = "starting";
    public BacktestResult? Result { get; set; }
    public string? Error { get; set; }

    // P0.2 (F5, Q5): teardown/persistence anomalies collected during a run that still produced a
    // complete engine result. Non-empty => the run finalizes `completed-with-warnings`, never
    // `failed`. Thread-safe: teardown warnings are appended from the run's finally block.
    public ConcurrentQueue<RunWarning> Warnings { get; } = new();
    public string Symbol { get; init; } = "";
    public string Period { get; init; } = "";
    public ConcurrentQueue<string> LogLines { get; init; } = new();
    public CancellationTokenSource? CancellationSource { get; set; }
    public Task? RunTask { get; set; }
    public IHost? EngineHost { get; set; }

    // P2.1 (F8): user-cancel intent, separate from Status. Cancel() no longer writes a (lying)
    // terminal `cancelled` while the run is still finalizing — it records intent here and lets the
    // run's own finalize path make the truthful terminal transition through the state machine. Also
    // lets the OperationCanceled path distinguish a user cancel from a near-completion timeout/teardown.
    public volatile bool CancelRequested;
    public int BarCount;
    public int BarsTotal { get; set; }
    public string SimTime { get; set; } = "";
    public IReadOnlyList<string> GetLogs() => LogLines.ToArray();

    // iter-21 U1 — live funnel counters + a small ring of recent journal lines for the
    // RunProgress envelope. A Progress<T> created on a thread with no captured SyncContext
    // posts its callbacks to the thread pool, so these can fire concurrently.
    public int Signals;
    public int Orders;
    public int Fills;
    public int Closes;
    public int Rejections;
    public int Breaches;

    // iter-24/21 — engine equity snapshot fields, populated from AccountSnapshotStore
    // after the run completes for the final RunProgress envelope.
    public decimal Equity;
    public decimal Balance;
    public bool HasEquityObservation;
    public decimal DailyDdPct;
    public decimal MaxDdPct;
    public decimal DistanceToDailyLimit;
    public int OpenPositions;
    public string? GovernorState;
    public string? GovernorReason;

    // iter-strategy-system P1/P3: which multi-pass combination is running now (for the live Monitor).
    public string? CurrentPass;
    public int PassIndex;
    public int PassTotal;

    // P4: config fields for memory-first run detail (no DB read while running).
    public string? Venue;
    public bool GovernorEnabled = true;
    public bool RegimeEnabled = true;
    public double CommissionPerMillion;
    public double SpreadPips;

    // iter-tape-trust T0/B2: memory-served run detail must carry these.
    public decimal InitialBalance;
    public DateTime BacktestFrom;
    public DateTime BacktestTo;
    public string? RiskProfileId;
    public string? EffectiveConfigJson;
    public string? RunPlanJson;

    // iter-tape-trust T0/F8: which exit resolution the tape venue actually used.
    public string? ExitResolution;

    // Tape replay playback speed (see TapeReplayAdapter.Speed).
    public float Speed = 10f;

    // Reference to the tape adapter for live speed changes.
    public TapeReplayAdapter? TapeAdapter;

    // P3.2: exploration mode — one-click preset run (SL=ATR×4, TP=none, add-ons off).
    public bool ExplorationMode;

    // P3.2: whether excursion paths were recorded for this run (tape-only, opt-in).
    public bool RecordExcursions;
}

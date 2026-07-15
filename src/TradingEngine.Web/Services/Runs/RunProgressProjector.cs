using TradingEngine.Domain;

namespace TradingEngine.Web.Services;

/// <summary>Pure projection from a run's in-memory state to the throttled SignalR envelope, plus
/// the funnel-counter tally. No I/O, no locks beyond Interlocked — safe to call from any thread.</summary>
public static class RunProgressProjector
{
    // iter-21 U1 — project the live run state into the throttled SignalR envelope. Fields the
    // orchestrator can't yet source (equity curve, governor, daily-DD) stay at honest zero/null
    // until iter-20 wires the kernel; the page renders an empty-state rather than fabricating.
    public static RunProgress Build(BacktestRunState state, string status)
    {
        DateTime? simTime = DateTime.TryParse(state.SimTime, out var t) ? t : null;
        var elapsedMs = (long)(DateTime.UtcNow - state.StartedAt).TotalMilliseconds;
        var barsPerSec = elapsedMs > 0 ? state.BarCount / (elapsedMs / 1000.0) : 0;

        var barsTotal = state.BarsTotal > 0 ? state.BarsTotal : 0;
        double percent;
        double? etaSeconds;
        if (status is "completed" or "completed-with-warnings")
        {
            percent = 100.0;
            etaSeconds = 0;
        }
        else if (barsTotal > 0 && state.BarCount > 0)
        {
            percent = state.BarCount >= barsTotal ? 99.9 : (double)state.BarCount / barsTotal * 100.0;
            etaSeconds = barsPerSec > 0 ? (barsTotal - state.BarCount) / barsPerSec : null;
        }
        else
        {
            percent = 0;
            etaSeconds = null;
        }

        return new RunProgress(
            state.RunId, status, simTime,
            BarsProcessed: state.BarCount, BarsTotal: barsTotal, Percent: percent, EtaSeconds: etaSeconds,
            WallElapsedMs: elapsedMs, BarsPerSec: barsPerSec, Speed: state.Speed,
            Equity: state.HasEquityObservation ? state.Equity : null,
            Balance: state.Balance, OpenPositions: state.OpenPositions,
            DailyDdPct: state.DailyDdPct, MaxDdPct: state.MaxDdPct,
            DistanceToDailyLimit: state.DistanceToDailyLimit,
            GovernorState: state.GovernorState, GovernorReason: state.GovernorReason,
            Counters: new RunCounters(state.Signals, state.Orders, state.Fills,
                state.Closes, state.Rejections, state.Breaches),
            CurrentPass: state.CurrentPass, PassIndex: state.PassIndex, PassTotal: state.PassTotal);
    }

    public static void TallyEvent(BacktestRunState state, BacktestProgressEvent evt)
    {
        // Counter keys must match the event-type strings the ENGINE actually emits:
        //   TradingLoop → "SIGNAL"/"ORDER"; MarketEventSource → "EXEC" (fill) / "REJECTED";
        //   EffectExecutor → "CLOSE" (on trade close); AccountProcessor → "BREACH".
        // The old keys ("FILL"/"REJECT"/no breach producer) never matched, so Fills/Rejections/
        // Breaches were always 0 and Closes undercounted.
        // Interlocked: a Progress<T> created on a thread with no captured SyncContext (the background
        // RunAsync task) posts its callbacks to the thread pool, so these can fire concurrently.
        switch (evt.EventType)
        {
            case "SIGNAL": Interlocked.Increment(ref state.Signals); break;
            case "ORDER": Interlocked.Increment(ref state.Orders); break;
            case "EXEC": Interlocked.Increment(ref state.Fills); break;
            case "CLOSE": Interlocked.Increment(ref state.Closes); break;
            case "REJECTED": case "OrderRejected": Interlocked.Increment(ref state.Rejections); break;
            case "BREACH": Interlocked.Increment(ref state.Breaches); break;
        }
    }
}

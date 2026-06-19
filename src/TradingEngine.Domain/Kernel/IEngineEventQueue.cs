namespace TradingEngine.Domain;

/// <summary>
/// The single-threaded, in-order event queue at the heart of the kernel funnel (iter-35 A2).
///
/// Tape events (bars/ticks) are enqueued in order. Processing an event through the reducer yields
/// effects; executing an effect against the venue (submit/close) produces <b>feedback events</b>
/// (<see cref="OrderFilled"/>, <see cref="OrderCancelled"/>, <see cref="EquityObserved"/>, …) which the
/// effect executor enqueues back here via <see cref="Enqueue"/>. The driver fully drains the queue
/// (tape event + all its feedback) before advancing to the next tape event.
///
/// This deterministic, single-reader drain is precisely what makes a backtest <b>bit-identical on
/// replay</b>: there is one total order of events and no concurrency in the decision path. The live
/// path uses the same queue, fed instead by the NetMQ transport — so live inherits the same kernel.
/// </summary>
public interface IEngineEventQueue
{
    /// <summary>Enqueue an event (from the tape, or as venue feedback during effect execution).</summary>
    void Enqueue(EngineEvent evt);

    /// <summary>Dequeue the next event in FIFO order. Returns false when empty.</summary>
    bool TryDequeue(out EngineEvent evt);
}

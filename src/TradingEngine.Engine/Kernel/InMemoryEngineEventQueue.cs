using TradingEngine.Domain;

namespace TradingEngine.Engine;

/// <summary>
/// The default in-order FIFO event queue for the kernel funnel (iter-35 A2). Single-threaded by design:
/// the <see cref="KernelDriver"/> is the only reader, and effect execution enqueues feedback events on
/// the same thread. That single total order is what makes a backtest bit-identical on replay.
/// </summary>
public sealed class InMemoryEngineEventQueue : IEngineEventQueue
{
    private readonly Queue<EngineEvent> _queue = new();

    public void Enqueue(EngineEvent evt) => _queue.Enqueue(evt);

    public bool TryDequeue(out EngineEvent evt)
    {
        if (_queue.Count > 0) { evt = _queue.Dequeue(); return true; }
        evt = null!;
        return false;
    }
}

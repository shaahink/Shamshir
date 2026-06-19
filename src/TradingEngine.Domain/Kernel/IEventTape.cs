namespace TradingEngine.Domain;

/// <summary>
/// A recorded, replayable source of <see cref="EngineEvent"/>s for one <see cref="DatasetRef"/>
/// (iter-35 A1/A4). This is the decoupling that makes backtests replayable: the <i>data</i> (the tape)
/// is independent of the <i>config</i> (strategy/risk). Re-running the same tape with a different
/// <see cref="ConfigSet"/> is "re-run this backtest with a different strategy / risk profile".
///
/// Bars now; ticks later — the granularity lives on <see cref="DatasetRef.Granularity"/>. A bar tape
/// yields <see cref="BarClosed"/> events in OpenTime order; a tick tape (future) yields
/// <see cref="TickReceived"/>. Account/equity feedback events are produced by the venue during effect
/// execution and re-enter via <see cref="IEngineEventQueue"/> — they are NOT part of the tape.
/// </summary>
public interface IEventTape
{
    DatasetRef Dataset { get; }

    /// <summary>Stream the recorded events in deterministic order. Implementations must be pure replays
    /// of stored data (no live clock, no randomness) so the kernel output is reproducible.</summary>
    IAsyncEnumerable<EngineEvent> ReadAsync(CancellationToken ct);
}

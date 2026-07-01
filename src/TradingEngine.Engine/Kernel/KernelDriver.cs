using System.Text.Json;
using TradingEngine.Domain;

namespace TradingEngine.Engine;

/// <summary>
/// THE FUNNEL (iter-35 A2). The single, deterministic pump that drives the whole engine:
///
///   tape event ─► queue ─► EngineReducer.Apply (PURE) ─► (state', effects[])
///                              │                               │
///                              ▼                               ▼
///                         StepRecord ─► IJournalWriter    IEffectExecutor (the ONLY I/O)
///                         (the journal)                   submit/close/modify → venue
///                                                         venue feedback (fills, account) ──┐
///                                                              re-enqueued via the queue ◄───┘
///
/// Both the backtest and the live path run through THIS driver. The only difference is the
/// <see cref="IEventTape"/> (recorded bars vs the NetMQ transport) — the decision path is identical, so
/// live inherits every correctness + journal property the backtest has. There is no replay-only fork.
///
/// <b>Why this shape is the whole design:</b>
///  • Determinism — one total event order, single-reader drain, a pure reducer (no wall-clock / no
///    Guid.NewGuid in the reducer — NEW-10). Same <see cref="RunSpec"/> ⇒ bit-identical journal.
///  • Replay — swap the <see cref="ConfigSet"/> over the same tape and re-run; that IS "repeat this
///    backtest with a different strategy / risk profile".
///  • Journal — one <see cref="StepRecord"/> per processed event, lossless, the single source of truth
///    for the report, the NDJSON download, and the live monitor tail (replaces the 4 sinks, NEW-4).
///
/// STATUS: skeleton for handover. The loop + journaling are real; the marked seams are for DeepSeek.
/// </summary>
public sealed class KernelDriver
{
    private readonly IKernel _kernel;
    private readonly IEngineEventQueue _queue;
    private readonly IJournalWriter _journal;
    private readonly IEffectExecutor _effects;
    private readonly string _runId;

    // Captures balance/equity/floating + DD/governor/protection into a RiskSnapshot for the StepRecord.
    // Folds the latest observed equity (from EquityObserved/AccountUpdate) onto the kernel state slices.
    private readonly Func<EngineState, RiskSnapshot> _captureRisk;

    // Optional per-event provider of the strategy evaluation verdicts (OBS-02) and current regime,
    // produced by the evaluator stage (A5) that also emits OrderProposed. Null = none attached.
    private readonly Func<EngineEvent, EngineState, (string? Regime, IReadOnlyList<StrategyVerdict> Verdicts)>? _evaluatorView;

    // TODO(deepseek): replace ad-hoc serialization with a single configured JsonSerializerOptions
    // (JsonStringEnumConverter, no reference loops, decimals as numbers) shared across the journal so the
    // NDJSON export is stable and diff-able. Stable serialization is part of the determinism contract.
    private static readonly JsonSerializerOptions _json = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public KernelDriver(
        IKernel kernel,
        IEngineEventQueue queue,
        IJournalWriter journal,
        IEffectExecutor effects,
        string runId,
        Func<EngineState, RiskSnapshot>? captureRisk = null,
        Func<EngineEvent, EngineState, (string?, IReadOnlyList<StrategyVerdict>)>? evaluatorView = null)
    {
        _kernel = kernel;
        _queue = queue;
        _journal = journal;
        _effects = effects;
        _runId = runId;
        _captureRisk = captureRisk ?? RiskSnapshots.Capture;
        _evaluatorView = evaluatorView;
    }

    /// <summary>
    /// Drive the kernel over a tape to completion, returning the final state.
    /// Drains all feedback events for a tape event before advancing — the in-order property that makes
    /// replay deterministic.
    /// </summary>
    public async Task<EngineState> RunAsync(IEventTape tape, EngineState initial, CancellationToken ct)
    {
        var state = initial;
        long seq = 0;

        await foreach (var tapeEvent in tape.ReadAsync(ct))
        {
            _queue.Enqueue(tapeEvent);

            // Drain this tape event AND every feedback event it triggers (fills, account updates,
            // force-close cascades) before pulling the next bar/tick.
            while (_queue.TryDequeue(out var evt))
            {
                ct.ThrowIfCancellationRequested();

                // PURE step. The kernel (and the EngineReducer it delegates to) must not perform I/O,
                // read wall-clock, or mint ids — that is what makes the run reproducible (NEW-10).
                EngineDecision decision = _kernel.Decide(state, evt);
                state = decision.State;

                // ONE journal record per processed event — lossless, sim-time anchored.
                _journal.Append(BuildStepRecord(++seq, evt, decision, state));

                // The ONLY place effects touch the world. The executor may enqueue feedback events
                // (e.g. a SubmitOrder → venue → OrderFilled) back onto _queue so they are processed
                // deterministically within this same drain.
                for (var i = 0; i < decision.Effects.Count; i++)
                {
                    await _effects.ExecuteAsync(decision.Effects[i], ct);
                }
            }
        }

        await _journal.FlushAsync(ct);
        return state;
    }

    private StepRecord BuildStepRecord(long seq, EngineEvent evt, EngineDecision decision, EngineState state)
    {
        var effectKinds = new string[decision.Effects.Count];
        for (var i = 0; i < decision.Effects.Count; i++)
        {
            effectKinds[i] = decision.Effects[i].GetType().Name;
        }

        var (regime, verdicts) = _evaluatorView?.Invoke(evt, state)
            ?? (null, (IReadOnlyList<StrategyVerdict>)Array.Empty<StrategyVerdict>());

        // TODO(deepseek): the gate verdict reason (accept/reject + why) should come off the decision —
        // surface it from PreTradeGate via a RecordDecisionEvent effect or a typed field, and thread it
        // here instead of null. Apply the Q5 indicator-sampling policy to `verdicts` before journaling.
        return new StepRecord(
            RunId: _runId,
            Seq: seq,
            SimTimeUtc: evt.OccurredAtUtc,
            EventKind: evt.GetType().Name,
            EventJson: JsonSerializer.Serialize(evt, evt.GetType(), _json),
            EffectKinds: effectKinds,
            EffectsJson: JsonSerializer.Serialize(decision.Effects, _json),
            Risk: _captureRisk(state),
            Regime: regime,
            DecisionReason: null,
            StrategyVerdicts: verdicts);
    }
}

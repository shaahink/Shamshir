using System.Text.Json;
using TradingEngine.Engine;

namespace TradingEngine.Host;

/// <summary>
/// The kernel-driven backtest loop (iter-36 K3). A single <see cref="RunAsync"/> that drives
/// <c>BarTape → (BarEvaluator + venue-feedback bridge) → Kernel → IEffectExecutor → venue</c>, with
/// <see cref="EngineState"/> as the single authority for positions / drawdown / protection / governor.
/// It reproduces the golden run through the real evaluator + gate + effect executor + a venue's
/// execution stream — proving the cutover is behaviour-preserving end-to-end before K4 flips the default.
///
/// Per bar:
///   1. advance the venue (clock/price/resting-limit matching);
///   2. evaluate the bar → <see cref="OrderProposed"/> events (K1);
///   3. pump: drain the kernel queue (Decide → journal → effects-to-venue), interleaving the venue's
///      execution feedback (fills/closes/cancels → kernel events, K2) until quiescent;
///   4. observe equity → <see cref="EquityObserved"/> (realized model for golden parity, or the venue's
///      mark-to-market account stream for production) → pump again (drawdown + breach watchdog).
///
/// The old <c>EngineRunner.RunBacktestLoopAsync</c> stays present but unused-by-default until K4.
/// </summary>
public sealed class KernelBacktestLoop
{
    private readonly IKernel _kernel;
    private readonly BarEvaluator _evaluator;
    private readonly IEffectExecutor _effects;
    private readonly IBrokerAdapter _venue;
    private readonly IEngineEventQueue _queue;
    private readonly IJournalWriter _journal;
    private readonly Func<EngineState, RiskSnapshot> _captureRisk;
    private readonly Action<Bar> _advanceVenue;
    private readonly Func<decimal>? _realizedEquity;
    private readonly Action<Bar, EngineState>? _onBarProcessed;
    private readonly Action<EngineEvent>? _onEvent;
    private readonly Func<Bar, EngineState, TrailingDecisions>? _evaluateTrailing;
    private readonly ResetConfig? _resetConfig;
    private readonly decimal _initialBalance;
    private readonly string _runId;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    private long _seq;
    private DateTime? _prevBarSimUtc;

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public KernelBacktestLoop(
        IKernel kernel,
        BarEvaluator evaluator,
        IEffectExecutor effects,
        IBrokerAdapter venue,
        IEngineEventQueue queue,
        IJournalWriter journal,
        Action<Bar> advanceVenue,
        decimal initialBalance,
        string runId,
        Microsoft.Extensions.Logging.ILogger logger,
        Func<EngineState, RiskSnapshot>? captureRisk = null,
        Func<decimal>? realizedEquity = null,
        Action<Bar, EngineState>? onBarProcessed = null,
        Action<EngineEvent>? onEvent = null,
        Func<Bar, EngineState, TrailingDecisions>? evaluateTrailing = null,
        ResetConfig? resetConfig = null)
    {
        _kernel = kernel;
        _evaluator = evaluator;
        _effects = effects;
        _venue = venue;
        _queue = queue;
        _journal = journal;
        _advanceVenue = advanceVenue;
        _initialBalance = initialBalance;
        _runId = runId;
        _logger = logger;
        _captureRisk = captureRisk ?? RiskSnapshots.Capture;
        _realizedEquity = realizedEquity;
        _onBarProcessed = onBarProcessed;
        _onEvent = onEvent;
        _evaluateTrailing = evaluateTrailing;
        _resetConfig = resetConfig;
    }

    /// <summary>Drive a recorded tape (tests / deterministic replay) to completion.</summary>
    public async Task<EngineState> RunAsync(IEventTape tape, EngineState initial, CancellationToken ct)
    {
        var state = initial;

        await foreach (var tapeEvent in tape.ReadAsync(ct))
        {
            if (tapeEvent is BarClosed bar)
            {
                state = await ProcessBarAsync(bar, state, ct);
            }
            else
            {
                // Non-bar tape events (future: ticks) just go through the pump.
                _queue.Enqueue(tapeEvent);
                state = await PumpAsync(state, ct);
            }
        }

        await _journal.FlushAsync(ct);
        return state;
    }

    /// <summary>
    /// Drive the <b>live or backtest</b> engine off the venue's <see cref="IBrokerAdapter.BarStream"/> —
    /// the mode-agnostic production entry point (iter-36 K4). Both <c>BacktestReplayAdapter</c> and the
    /// cTrader adapter publish bars on that stream, so the same kernel loop serves both; the only
    /// difference is the adapter. Runs until the bar stream completes.
    /// </summary>
    public async Task<EngineState> RunFromBrokerAsync(EngineState initial, CancellationToken ct)
    {
        var state = initial;

        await foreach (var bar in _venue.BarStream.ReadAllAsync(ct))
        {
            var barClosed = new BarClosed(bar.Symbol, bar.Timeframe, bar.Open, bar.High, bar.Low, bar.Close, bar.OpenTimeUtc);
            state = await ProcessBarAsync(barClosed, state, ct);
        }

        // Tail-drain: after the bar loop, drain any late-arriving venue execution frames (e.g. cTrader's
        // autonomous end-of-run flatten sends a close exec after the last bar). The replay venue has no
        // late autonomous execs, so this keeps the golden path byte-identical. One drain serves both venues.
        state = await PumpAsync(state, ct);

        await _journal.FlushAsync(ct);
        return state;
    }

    /// <summary>One bar through the kernel: advance the venue, drain prior feedback, evaluate → proposals,
    /// pump to quiescence, observe equity, pump again. The single per-bar unit shared by the tape driver
    /// and the broker-stream driver.</summary>
    private async Task<EngineState> ProcessBarAsync(BarClosed bar, EngineState state, CancellationToken ct)
    {
        var barModel = new Bar(bar.Symbol, bar.Timeframe, bar.BarOpenTimeUtc, bar.Open, bar.High, bar.Low, bar.Close, 0);
        _advanceVenue(barModel);

        // Drain any venue feedback the bar-advance produced (resting-limit fills/expiries, the per-bar
        // mark-to-market account update) before evaluating this bar.
        state = await PumpAsync(state, ct);

        // iter-redesign-ctrader P2.1: reconcile the engine's live book to the venue's open set after
        // draining feedback. If the venue closed a position the engine still holds Open (e.g. cTrader
        // server-side SL/TP), force-resolve it so the gate sees the reconciled live set — not a stale
        // book accumulating leaked positions. Emit journal-only effects (no venue close commands).
        if (state.ExitMode == ExitMode.VenueManaged)
        {
            var venueOpenIds = _venue.GetOpenPositionIds();
            var lastPrice = bar.Close > 0 ? new Price(bar.Close) : new Price(0m);
            var rec = EngineReducer.ReconcileToVenue(state, venueOpenIds, lastPrice);
            if (rec.Effects.Count > 0)
            {
                state = rec.State;
                var step = new StepRecord(
                    RunId: _runId,
                    Seq: Interlocked.Increment(ref _seq),
                    SimTimeUtc: bar.BarOpenTimeUtc,
                    EventKind: "Reconcile",
                    EventJson: System.Text.Json.JsonSerializer.Serialize(new { venueOpenIds = venueOpenIds.Count }),
                    EffectKinds: rec.Effects.Select(e => e.GetType().Name).ToArray(),
                    EffectsJson: System.Text.Json.JsonSerializer.Serialize(rec.Effects),
                    Risk: _captureRisk(state),
                    Regime: null,
                    DecisionReason: "RECONCILED",
                    StrategyVerdicts: Array.Empty<StrategyVerdict>());
                _journal.Append(step);
            }
        }

        // iter-36 NEW-1 / K-GAP-1: emit the prop-firm day/week/month roll BEFORE evaluating this bar, when
        // its sim-time crosses the reset boundary. The reducer re-bases drawdown to the new period's opening
        // equity (state.Account.Equity — just refreshed by the drain above) and resets the governor + clears
        // boundary-scoped protection (Kernel.DecideReset), fixing C4/H7 for multi-day runs. No-op when no
        // reset clock is configured (the golden/unit harnesses), so single-day runs stay byte-identical.
        if (_resetConfig is { } resetCfg)
        {
            var rolls = ResetClock.Crossed(_prevBarSimUtc, bar.BarOpenTimeUtc, resetCfg);
            if (rolls.Any)
            {
                if (rolls.Month) _queue.Enqueue(new MonthRolled(bar.BarOpenTimeUtc));
                if (rolls.Week) _queue.Enqueue(new WeekRolled(bar.BarOpenTimeUtc));
                if (rolls.Day) _queue.Enqueue(new DayRolled(bar.BarOpenTimeUtc));
                state = await PumpAsync(state, ct);
            }
        }
        _prevBarSimUtc = bar.BarOpenTimeUtc;

        var eval = await _evaluator.EvaluateAsync(bar, state, ct);
        foreach (var proposal in eval.Proposals)
        {
            _queue.Enqueue(proposal);
        }

        // iter-redesign P1.2: pump the proposals FIRST so each accepted entry's fill drains off the venue's
        // ExecutionStream and the position reaches Open BEFORE this bar's BarClosed is applied. Previously the
        // proposal and the BarClosed were pumped together, so BarClosed hit a still-Submitted position →
        // (Submitted, BarClosed) illegal-transition records (the "85"), and a freshly entered position could
        // never exit on its own entry bar. Draining entry fills first makes the position Open in time, so the
        // FSM only ever sees legal (Open, BarClosed) arms and same-bar SL/TP detection works.
        state = await PumpAsync(state, ct);

        _queue.Enqueue(bar);
        state = await PumpAsync(state, ct);

        // Equity → drawdown + breach. Realized model (initial + closed net PnL) for golden parity;
        // production uses the venue's mark-to-market account stream (drained inside the pump).
        if (_realizedEquity is not null)
        {
            _queue.Enqueue(new EquityObserved(_initialBalance, _realizedEquity(), 0m, bar.BarOpenTimeUtc));
            state = await PumpAsync(state, ct);
        }

        // Trailing / breakeven (iter-36 K4 gap-3). Runs at the END of the bar — after entries + exit
        // detection — so a trailed stop only takes effect on the NEXT bar (no intrabar look-ahead), exactly
        // as the imperative TradingLoop.UpdateTrailingStopsAsync did. The decision is impure (recent bars +
        // per-position config); the reducer applies it purely (update the stop + emit ModifyStopLoss).
        if (_evaluateTrailing is not null)
        {
            var decisions = _evaluateTrailing(barModel, state);
            if (decisions.Moves.Count > 0 || decisions.Partials.Count > 0 || decisions.Resolutions.Count > 0)
            {
                // iter-38 A7: journal ADDON_RESOLVED first so the resolved numbers precede any TRAIL/PARTIAL.
                for (var i = 0; i < decisions.Resolutions.Count; i++)
                    _queue.Enqueue(new AddOnsResolved(decisions.Resolutions[i].PositionId, decisions.Resolutions[i].DetailJson, bar.BarOpenTimeUtc));
                for (var i = 0; i < decisions.Moves.Count; i++)
                    _queue.Enqueue(new StopLossModifyRequested(decisions.Moves[i].PositionId, decisions.Moves[i].NewStopLoss, bar.BarOpenTimeUtc, decisions.Moves[i].Kind));
                // iter-38 A4b: PartialTp partial-close requests run after the stop moves (deterministic order).
                for (var i = 0; i < decisions.Partials.Count; i++)
                    _queue.Enqueue(new PartialCloseRequested(decisions.Partials[i].PositionId, decisions.Partials[i].CloseLots, decisions.Partials[i].Reason, bar.BarOpenTimeUtc));
                state = await PumpAsync(state, ct);
            }
        }

        await _venue.CompleteBarAsync(ct);

        // Production hook: report bar progress (count + sim clock) and persist equity/snapshots from the
        // authoritative EngineState. Null in tests.
        _onBarProcessed?.Invoke(barModel, state);
        return state;
    }

    /// <summary>
    /// Drain the kernel queue to quiescence, interleaving venue feedback. Each kernel step: Decide → one
    /// StepRecord → execute effects (which reach the venue). After the queue empties, drain the venue's
    /// execution stream (and, in mark-to-market mode, the account stream) back into kernel events and keep
    /// going until nothing more is produced — the in-order property that makes the run deterministic.
    /// </summary>
    private async Task<EngineState> PumpAsync(EngineState state, CancellationToken ct)
    {
        var progressed = true;
        while (progressed)
        {
            progressed = false;

            while (_queue.TryDequeue(out var evt))
            {
                ct.ThrowIfCancellationRequested();
                progressed = true;

                var decision = _kernel.Decide(state, evt);
                state = decision.State;
                _journal.Append(BuildStepRecord(++_seq, evt, decision, state));

                // iter-38 B1: feed per-event progress so the live-monitor counters aren't stuck at 0.
                try { _onEvent?.Invoke(evt); }
                catch { /* best-effort progress — never disrupt the kernel pump */ }

                for (var i = 0; i < decision.Effects.Count; i++)
                {
                    await _effects.ExecuteAsync(decision.Effects[i], ct);
                }
            }

            // Venue execution feedback → kernel events (fills move Submitted→Open; close fills close
            // Open positions → PublishTradeClosed; cancels/rejects terminate the position).
            while (_venue.ExecutionStream.TryRead(out var exec))
            {
                // iter-37 K-GAP-6: prefer the symbol the venue stamped on the execution (multi-symbol
                // correct); only fall back to resolving by id when the venue didn't carry it.
                var symbol = exec.Symbol ?? ResolveSymbol(state, exec.OrderId);
                var evt = KernelFeedback.FromExecution(exec, symbol);
                if (evt is not null)
                {
                    progressed = true;
                    _queue.Enqueue(evt);
                }
            }

            // Mark-to-market account stream (production venue). Skipped when the caller drives a realized
            // equity model (golden parity), to keep the two equity semantics cleanly separate.
            if (_realizedEquity is null)
            {
                while (_venue.AccountStream.TryRead(out var acct))
                {
                    progressed = true;
                    _queue.Enqueue(KernelFeedback.FromAccount(acct));
                }
            }
        }

        return state;
    }

    // The venue execution event omits the symbol; resolve it from the kernel position the order belongs
    // to (present for entry fills — Submitted — and close fills — Open). Falls back to any open position's
    // symbol, then a parse of the run, so a stray event never throws.
    private static Symbol ResolveSymbol(EngineState state, Guid orderId)
    {
        if (state.Positions.TryGetValue(orderId, out var ps)) return ps.Symbol;
        foreach (var (_, p) in state.Positions) return p.Symbol;
        return Symbol.Parse("EURUSD");
    }

    private StepRecord BuildStepRecord(long seq, EngineEvent evt, EngineDecision decision, EngineState state)
    {
        var effectKinds = new string[decision.Effects.Count];
        string? decisionReason = null;
        for (var i = 0; i < decision.Effects.Count; i++)
        {
            var eff = decision.Effects[i];
            effectKinds[i] = eff.GetType().Name;
            // Surface the gate verdict (accept/reject + why) onto the StepRecord — the PreTradeGate reason
            // travels on the RecordDecisionEvent effect (iter-36 K5; was hard-coded null).
            if (decisionReason is null && eff is RecordDecisionEvent rde)
            {
                decisionReason = rde.Decision.Reason;
            }
        }

        // Fold the per-bar evaluator verdicts/regime onto the BarClosed record (the per-bar "why"). The
        // full Q5 sampling + DecisionReason threading lands in K5; here we attach what the evaluator just
        // produced for this bar.
        var (regime, verdicts) = evt is BarClosed
            ? (_evaluator.Latest.Regime, _evaluator.Latest.Verdicts)
            : (null, (IReadOnlyList<StrategyVerdict>)Array.Empty<StrategyVerdict>());

        return new StepRecord(
            RunId: _runId,
            Seq: seq,
            SimTimeUtc: evt.OccurredAtUtc,
            EventKind: EventKindFor(evt),
            EventJson: JsonSerializer.Serialize(evt, evt.GetType(), Json),
            EffectKinds: effectKinds,
            EffectsJson: JsonSerializer.Serialize(decision.Effects, Json),
            Risk: _captureRisk(state),
            Regime: regime,
            DecisionReason: decisionReason,
            StrategyVerdicts: verdicts);
    }

    // iter-38 A7: map add-on actions onto the canonical journal kinds the SPA's unified-journal (F1) and
    // per-bar "why" (F2) views filter on. Only events that NEVER occur on the default/golden path are
    // remapped (PartialTp etc. are add-on-gated), so the golden journal stays byte-identical.
    private static string EventKindFor(EngineEvent evt) => evt switch
    {
        StopLossModifyRequested s => s.Kind,
        PartialCloseRequested => AddOnJournalKinds.Partial,
        AddOnsResolved => AddOnJournalKinds.AddOnsResolved,
        _ => evt.GetType().Name,
    };
}

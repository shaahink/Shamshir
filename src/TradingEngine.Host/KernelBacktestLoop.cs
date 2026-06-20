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
    private readonly decimal _initialBalance;
    private readonly string _runId;
    private readonly Microsoft.Extensions.Logging.ILogger _logger;

    private long _seq;

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
        Func<decimal>? realizedEquity = null)
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
    }

    public async Task<EngineState> RunAsync(IEventTape tape, EngineState initial, CancellationToken ct)
    {
        var state = initial;

        await foreach (var tapeEvent in tape.ReadAsync(ct))
        {
            if (tapeEvent is not BarClosed bar)
            {
                // Non-bar tape events (future: ticks) just go through the pump.
                _queue.Enqueue(tapeEvent);
                state = await PumpAsync(state, ct);
                continue;
            }

            var barModel = new Bar(bar.Symbol, bar.Timeframe, bar.BarOpenTimeUtc, bar.Open, bar.High, bar.Low, bar.Close, 0);
            _advanceVenue(barModel);

            // Drain any venue feedback the bar-advance produced (resting-limit fills/expiries, the
            // per-bar mark-to-market account update) before evaluating this bar.
            state = await PumpAsync(state, ct);

            var eval = await _evaluator.EvaluateAsync(bar, state, ct);
            foreach (var proposal in eval.Proposals)
            {
                _queue.Enqueue(proposal);
            }
            _queue.Enqueue(bar);

            state = await PumpAsync(state, ct);

            // Equity → drawdown + breach. Realized model (initial + closed net PnL) for golden parity;
            // production uses the venue's mark-to-market account stream (drained inside the pump).
            if (_realizedEquity is not null)
            {
                _queue.Enqueue(new EquityObserved(_initialBalance, _realizedEquity(), 0m, bar.BarOpenTimeUtc));
                state = await PumpAsync(state, ct);
            }

            await _venue.CompleteBarAsync(ct);
        }

        await _journal.FlushAsync(ct);
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

                for (var i = 0; i < decision.Effects.Count; i++)
                {
                    await _effects.ExecuteAsync(decision.Effects[i], ct);
                }
            }

            // Venue execution feedback → kernel events (fills move Submitted→Open; close fills close
            // Open positions → PublishTradeClosed; cancels/rejects terminate the position).
            while (_venue.ExecutionStream.TryRead(out var exec))
            {
                var symbol = ResolveSymbol(state, exec.OrderId);
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
        for (var i = 0; i < decision.Effects.Count; i++)
        {
            effectKinds[i] = decision.Effects[i].GetType().Name;
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
            EventKind: evt.GetType().Name,
            EventJson: JsonSerializer.Serialize(evt, evt.GetType(), Json),
            EffectKinds: effectKinds,
            EffectsJson: JsonSerializer.Serialize(decision.Effects, Json),
            Risk: _captureRisk(state),
            Regime: regime,
            DecisionReason: null,
            StrategyVerdicts: verdicts);
    }
}

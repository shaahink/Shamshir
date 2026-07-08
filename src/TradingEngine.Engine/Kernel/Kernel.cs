using TradingEngine.Domain;

namespace TradingEngine.Engine;

/// <summary>
/// The concrete decision core (iter-35 A2). Closes over the run-constant <see cref="KernelConfig"/> and
/// routes each event. PURE: no I/O, no wall-clock, no Guid.NewGuid (ids/timestamps come off the event).
///
/// Implemented in this skeleton:
///   • OrderProposed  → PreTradeGate (the gate is now IN the kernel) → SubmitOrder + RegisterRisk, or reject.
///   • EquityObserved → drawdown/account fold (reducer) + breach watchdog (enter protection / force-close).
///   • Day/Week/Month → reducer resets (drawdown + governor) + protection-exit via ProtectionState.ClearsOn (C4/H7).
///   • everything else (Order*/Close/ForceCloseAll) → the existing pure EngineReducer.
///
/// NEW-10 determinism leak fixed: PositionLifecycle.CreateIntended now uses orderId as positionId
/// (no Guid.NewGuid), so identical (Dataset, ConfigSet, Seed) ⇒ bit-identical journal.
/// </summary>
public sealed class Kernel(KernelConfig config) : IKernel
{
    private readonly KernelConfig _config = config;

    public EngineDecision Decide(EngineState state, EngineEvent evt) => evt switch
    {
        OrderProposed proposed => DecideProposed(state, proposed),
        EquityObserved equity => DecideEquity(state, equity),
        DayRolled => DecideReset(state, evt, ProtectionBoundary.Day),
        WeekRolled => DecideReset(state, evt, ProtectionBoundary.Week),
        MonthRolled => DecideReset(state, evt, ProtectionBoundary.Month),
        _ => EngineReducer.Apply(state, evt),
    };

    private EngineDecision DecideProposed(EngineState state, OrderProposed p)
    {
        var symbol = _config.ResolveSymbol(p.Symbol);
        var open = _config.ProjectOpenPositions(state);
        // The impure verdicts (news/weekend/compliance/governor) were computed by the evaluator at
        // sim-time and carried on the proposal (iter-36 K1) — applying them here keeps the gate pure and
        // replay-deterministic, while ensuring no external protection is silently dropped.
        // iter-36 K4 gap-1: size with the proposal's resolved per-strategy profile (the evaluator resolved
        // it via intent.RiskProfileId); fall back to the run-constant profile when absent (direct-construct tests).
        var profile = p.Profile ?? _config.Profile;
        var gate = PreTradeGate.Evaluate(state, p, _config.Constraints, profile, _config.Sizing, symbol, open, p.External);

        if (!gate.Accepted)
        {
            // RunId/Seq left empty/0 — the journal writer stamps them (see PipelineEventWriter pattern).
            var reject = new DecisionRecord(
                RunId: "", SimTimeUtc: p.OccurredAtUtc, Seq: 0,
                Symbol: p.Symbol.Value, StrategyId: p.StrategyId, PhaseBefore: null,
                Event: "SignalRejected", GuardResult: gate.RejectReason, PhaseAfter: null,
                Reason: gate.RejectReason, DetailJson: "{}");
            return new EngineDecision(state, new EngineEffect[] { new RecordDecisionEvent(reject) });
        }

        // Accepted: create the Intended position via the existing lifecycle path, then emit the venue
        // submit + the risk registration. (OrderSubmitted is the event that builds the Intended position;
        // SubmitOrder is the effect that reaches the broker.)
        var submitted = new OrderSubmitted(
            p.OrderId, p.Symbol, p.Direction, gate.Lots, p.LimitPrice, p.StrategyId,
            p.OccurredAtUtc, p.OrderType,
            p.StopLoss, p.TakeProfit,
            EntryReason: p.EntryReason, EntryRegime: p.EntryRegime);
        var posDecision = EngineReducer.Apply(state, submitted);

        var positionId = posDecision.State.Positions.Values
            .FirstOrDefault(x => x.OrderId == p.OrderId)?.PositionId ?? p.OrderId;

        var effects = new List<EngineEffect>(posDecision.Effects)
        {
            new SubmitOrder(p.OrderId, p.Symbol, p.Direction, gate.Lots, p.LimitPrice, p.StopLoss, p.TakeProfit, p.StrategyId, p.OrderType, p.Entry),
            new RegisterRisk(positionId, p.StrategyId, gate.RiskAmount),
        };
        return new EngineDecision(posDecision.State, effects);
    }

    private EngineDecision DecideEquity(EngineState state, EquityObserved eq)
    {
        // Pure drawdown + account fold lives in the reducer; config-dependent breach policy is layered here.
        var baseDecision = EngineReducer.Apply(state, eq);
        var s = baseDecision.State;

        if (s.Protection.InProtectionMode)
        {
            return baseDecision; // already suspended; nothing new to do until a reset clears it.
        }

        var c = _config.Constraints;
        var flatten = (decimal)_config.Sizing.FlattenAtFraction;

        var (cause, reason) = EvaluateDrawdownBreach(s.Drawdown, c, flatten);

        if (cause == ProtectionCause.None)
        {
            return baseDecision;
        }

        s = s with { Protection = s.Protection.Enter(cause, reason) };

        var effects = new List<EngineEffect>(baseDecision.Effects);
        if (c.ForceCloseOnBreachEnabled && c.ForceCloseOnBreach)
        {
            // Flatten every open position by VENUE order id (mirrors EngineReducer.HandleForceCloseAll).
            foreach (var ps in s.Positions.Values)
            {
                effects.Add(new CloseOpenPosition(ps.OrderId, cause.ToString()));
            }
        }
        return new EngineDecision(s, effects);
    }

    /// <summary>
    /// Static breach-evaluation helper (AF4). Checks drawdown levels against constraints (toggle-gated),
    /// returning the most-severe breach cause (daily → max → weekly → monthly). Produces the same result
    /// as the instance <c>DecideEquity</c> path. Used by the imperative AccountProcessor watchdog during
    /// the cutover so both paths share one authority.
    /// </summary>
    public static (ProtectionCause Cause, string Reason) EvaluateDrawdownBreach(
        DrawdownState dd, ConstraintSet c, decimal flattenFraction)
    {
        if (c.DailyDdEnabled && dd.CurrentDailyDrawdown >= c.MaxDailyLoss * flattenFraction)
            return (ProtectionCause.DailyDrawdown, $"Daily DD breach: {dd.CurrentDailyDrawdown:P2} >= {c.MaxDailyLoss * flattenFraction:P2}");
        if (c.MaxDdEnabled && dd.CurrentMaxDrawdown >= c.MaxTotalLoss * flattenFraction)
            return (ProtectionCause.MaxDrawdown, $"Max DD breach: {dd.CurrentMaxDrawdown:P2} >= {c.MaxTotalLoss * flattenFraction:P2}");
        if (c.WeeklyDdEnabled && c.MaxWeeklyLoss > 0 && dd.CurrentWeeklyDrawdown >= c.MaxWeeklyLoss * flattenFraction)
            return (ProtectionCause.WeeklyDrawdown, $"Weekly DD breach: {dd.CurrentWeeklyDrawdown:P2} >= {c.MaxWeeklyLoss * flattenFraction:P2}");
        if (c.MonthlyDdEnabled && c.MaxMonthlyLoss > 0 && dd.CurrentMonthlyDrawdown >= c.MaxMonthlyLoss * flattenFraction)
            return (ProtectionCause.MonthlyDrawdown, $"Monthly DD breach: {dd.CurrentMonthlyDrawdown:P2} >= {c.MaxMonthlyLoss * flattenFraction:P2}");
        return (ProtectionCause.None, "");
    }

    private static EngineDecision DecideReset(EngineState state, EngineEvent evt, ProtectionBoundary boundary)
    {
        // Reducer handles the pure resets (drawdown re-base; on DayRolled also GovernorMachine.ApplyDailyReset
        // — which fixes H7: governor profit-lock now clears on the day roll through the kernel path).
        var baseDecision = EngineReducer.Apply(state, evt);
        var s = baseDecision.State;

        // C4: protection exits per ProtectionState.ClearsOn (MaxDD honors ResetPolicy; daily clears on day, etc.).
        if (s.Protection.InProtectionMode && s.Protection.ClearsOn(boundary))
        {
            s = s with { Protection = s.Protection.Clear() };
        }

        return new EngineDecision(s, baseDecision.Effects);
    }
}

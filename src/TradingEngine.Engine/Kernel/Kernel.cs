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
        var gate = PreTradeGate.Evaluate(state, p, _config.Constraints, _config.Profile, _config.Sizing, symbol, open);

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
            p.OccurredAtUtc, p.StopLoss, p.TakeProfit);
        var posDecision = EngineReducer.Apply(state, submitted);

        var positionId = posDecision.State.Positions.Values
            .FirstOrDefault(x => x.OrderId == p.OrderId)?.PositionId ?? p.OrderId;

        var effects = new List<EngineEffect>(posDecision.Effects)
        {
            new SubmitOrder(p.OrderId, p.Symbol, p.Direction, gate.Lots, p.LimitPrice, p.StopLoss, p.TakeProfit, p.StrategyId),
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
        var dd = s.Drawdown;

        // B1: breach watchdog gated per toggle. Order mirrors daily → max → weekly → monthly.
        var (cause, used, limit) =
            c.DailyDdEnabled && dd.CurrentDailyDrawdown >= c.MaxDailyLoss * flatten ? (ProtectionCause.DailyDrawdown, dd.CurrentDailyDrawdown, c.MaxDailyLoss)
            : c.MaxDdEnabled && dd.CurrentMaxDrawdown >= c.MaxTotalLoss * flatten ? (ProtectionCause.MaxDrawdown, dd.CurrentMaxDrawdown, c.MaxTotalLoss)
            : c.WeeklyDdEnabled && c.MaxWeeklyLoss > 0 && dd.CurrentWeeklyDrawdown >= c.MaxWeeklyLoss * flatten ? (ProtectionCause.WeeklyDrawdown, dd.CurrentWeeklyDrawdown, c.MaxWeeklyLoss)
            : c.MonthlyDdEnabled && c.MaxMonthlyLoss > 0 && dd.CurrentMonthlyDrawdown >= c.MaxMonthlyLoss * flatten ? (ProtectionCause.MonthlyDrawdown, dd.CurrentMonthlyDrawdown, c.MaxMonthlyLoss)
            : (ProtectionCause.None, 0m, 0m);

        if (cause == ProtectionCause.None)
        {
            return baseDecision;
        }

        var reason = $"{cause} breach: {used:P2} >= {limit * flatten:P2}";
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

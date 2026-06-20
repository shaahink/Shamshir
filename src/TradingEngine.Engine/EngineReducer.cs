using TradingEngine.Domain;

namespace TradingEngine.Engine;

public static class EngineReducer
{
    public static EngineDecision Apply(EngineState state, EngineEvent evt)
    {
        var effects = new List<EngineEffect>();

        switch (evt)
        {
            case OrderSubmitted submitted:
                return HandleOrderSubmitted(state, submitted);

            case OrderFilled filled:
                return HandleOrderFilled(state, filled, effects);

            case OrderPartiallyFilled partial:
                return HandleOrderPartiallyFilled(state, partial, effects);

            case OrderRejected rejected:
                return HandleOrderRejected(state, rejected);

            case OrderCancelled cancelled:
                return HandleOrderCancelled(state, cancelled);

            case CloseRequested close:
                return HandleCloseRequested(state, close);

            case BarClosed bar:
                return HandleBarClosed(state, bar, effects);

            case TickReceived tick:
                return HandleTickReceived(state, tick, effects);

            case EquityObserved equity:
                return HandleEquityObserved(state, equity);

            case DayRolled day:
                return HandleDayRolled(state, day);

            case WeekRolled week:
                return HandleWeekRolled(state, week);

            case MonthRolled month:
                return HandleMonthRolled(state, month);

            case ForceCloseAllRequested forceClose:
                return HandleForceCloseAll(state, forceClose);

            default:
                return new EngineDecision(state, effects);
        }
    }

    private static EngineDecision HandleOrderSubmitted(EngineState state, OrderSubmitted evt)
    {
        var posState = PositionLifecycle.CreateIntended(
            evt.OrderId, evt.Symbol, evt.Direction,
            evt.Lots, evt.LimitPrice, evt.StopLoss, evt.TakeProfit, evt.StrategyId);

        var (nextPos, posEffects) = PositionLifecycle.Apply(posState, evt);

        var newPositions = new Dictionary<Guid, PositionState>(state.Positions)
        {
            [nextPos.PositionId] = nextPos
        };

        var nextState = state with
        {
            Positions = newPositions,
            OpenPositionCount = state.OpenPositionCount + 1
        };

        var effects = new List<EngineEffect>(posEffects);
        return new EngineDecision(nextState, effects);
    }

    private static EngineDecision HandleOrderFilled(EngineState state, OrderFilled evt, List<EngineEffect> effects)
    {
        var posState = FindPositionByOrderId(state, evt.OrderId);
        if (posState is null)
        {
            return new EngineDecision(state, effects);
        }

        var (nextPos, posEffects) = PositionLifecycle.Apply(posState, evt);
        effects.AddRange(posEffects);

        var newPositions = new Dictionary<Guid, PositionState>(state.Positions);
        if (nextPos.Phase == PositionPhase.Closed)
        {
            newPositions.Remove(nextPos.PositionId);
        }
        else
        {
            newPositions[nextPos.PositionId] = nextPos;
        }

        var nextState = state with
        {
            Positions = newPositions,
            OpenPositionCount = nextPos.Phase is PositionPhase.Closed or PositionPhase.Rejected
                ? state.OpenPositionCount - 1
                : state.OpenPositionCount
        };

        return new EngineDecision(nextState, effects);
    }

    private static EngineDecision HandleOrderPartiallyFilled(EngineState state, OrderPartiallyFilled evt, List<EngineEffect> effects)
    {
        var posState = FindPositionByOrderId(state, evt.OrderId);
        if (posState is null)
        {
            return new EngineDecision(state, effects);
        }

        var (nextPos, posEffects) = PositionLifecycle.Apply(posState, evt);
        effects.AddRange(posEffects);

        var newPositions = new Dictionary<Guid, PositionState>(state.Positions)
        {
            [nextPos.PositionId] = nextPos
        };

        return new EngineDecision(state with { Positions = newPositions }, effects);
    }

    private static EngineDecision HandleOrderRejected(EngineState state, OrderRejected evt)
    {
        var posState = FindPositionByOrderId(state, evt.OrderId);
        if (posState is null)
        {
            return new EngineDecision(state, []);
        }

        var (nextPos, posEffects) = PositionLifecycle.Apply(posState, evt);

        var newPositions = new Dictionary<Guid, PositionState>(state.Positions);
        newPositions.Remove(nextPos.PositionId);

        var nextState = state with
        {
            Positions = newPositions,
            OpenPositionCount = state.OpenPositionCount - 1
        };

        var effects = new List<EngineEffect>(posEffects);
        return new EngineDecision(nextState, effects);
    }

    private static EngineDecision HandleOrderCancelled(EngineState state, OrderCancelled evt)
    {
        var posState = FindPositionByOrderId(state, evt.OrderId);
        if (posState is null)
        {
            return new EngineDecision(state, []);
        }

        var (nextPos, posEffects) = PositionLifecycle.Apply(posState, evt);

        var newPositions = new Dictionary<Guid, PositionState>(state.Positions);
        newPositions.Remove(nextPos.PositionId);

        var nextState = state with
        {
            Positions = newPositions,
            OpenPositionCount = Math.Max(0, state.OpenPositionCount - 1)
        };

        return new EngineDecision(nextState, new List<EngineEffect>(posEffects));
    }

    private static EngineDecision HandleCloseRequested(EngineState state, CloseRequested evt)
    {
        if (!state.Positions.TryGetValue(evt.PositionId, out var posState))
        {
            return new EngineDecision(state, []);
        }

        var (nextPos, posEffects) = PositionLifecycle.Apply(posState, evt);

        var newPositions = new Dictionary<Guid, PositionState>(state.Positions)
        {
            [nextPos.PositionId] = nextPos
        };

        var effects = new List<EngineEffect>(posEffects);
        return new EngineDecision(state with { Positions = newPositions }, effects);
    }

    // iter-35 (A2): WIRED via Kernel.Decide → KernelDriver. The kernel feeds BarClosed events
    // from the tape, and this reducer branch owns SL/TP detection + GovernorMachine.ApplyBar.
    // Replaces EngineRunner.SimulateBarExitsAsync (Kill-List).
    private static EngineDecision HandleBarClosed(EngineState state, BarClosed evt, List<EngineEffect> effects)
    {
        var newPositions = new Dictionary<Guid, PositionState>(state.Positions);
        foreach (var (id, posState) in state.Positions)
        {
            if (posState.Symbol != evt.Symbol) continue;
            var (nextPos, posEffects) = PositionLifecycle.Apply(posState, evt);
            effects.AddRange(posEffects);

            if (nextPos.Phase is PositionPhase.Open or PositionPhase.Reducing)
            {
                var exit = DetectSlTpExit(nextPos, evt);
                if (exit is not null)
                {
                    // Carry the exit reason on the position so the venue close fill (an OrderFilled on an
                    // Open position) records "SL"/"TP" instead of "FORCE", and carry the stop/target price
                    // so the close fills there — matching the imperative SimulateBarExitsAsync (K2).
                    var exitPrice = exit == "TP" && nextPos.TakeProfit is { } tp
                        ? tp
                        : nextPos.CurrentStopLoss;
                    nextPos = nextPos with { CloseReason = exit };
                    effects.Add(new CloseOpenPosition(nextPos.OrderId, exit, exitPrice));
                }
            }

            newPositions[id] = nextPos;
        }

        var newGovernor = GovernorMachine.ApplyBar(state.Governor);

        return new EngineDecision(state with { Positions = newPositions, Governor = newGovernor }, effects);
    }

    public static string? DetectSlTpExit(PositionState state, BarClosed bar)
    {
        return DetectSlTpExit(state.Direction, state.CurrentStopLoss, state.TakeProfit,
            bar.High, bar.Low);
    }

    /// <summary>
    /// Stateless overload for callers that don't hold a full <see cref="PositionState"/> (e.g. the old
    /// PositionTracker loop during the AF3 cutover). Behaviour is byte-identical to the state-based overload.
    /// </summary>
    public static string? DetectSlTpExit(TradeDirection direction, Price stopLoss, Price? takeProfit, Bar bar)
    {
        return DetectSlTpExit(direction, stopLoss, takeProfit, bar.High, bar.Low);
    }

    private static string? DetectSlTpExit(TradeDirection direction, Price stopLoss, Price? takeProfit, decimal high, decimal low)
    {
        if (direction == TradeDirection.Long)
        {
            if (low <= stopLoss.Value) return "SL";
            if (takeProfit is not null && high >= takeProfit.Value.Value) return "TP";
        }
        else
        {
            if (high >= stopLoss.Value) return "SL";
            if (takeProfit is not null && low <= takeProfit.Value.Value) return "TP";
        }
        return null;
    }

    // iter-35 (A2): WIRED via Kernel.Decide. Ticks are fed from the tape (tick granularity, future)
    // and this reducer branch applies PositionLifecycle.Apply per position.
    private static EngineDecision HandleTickReceived(EngineState state, TickReceived evt, List<EngineEffect> effects)
    {
        var newPositions = new Dictionary<Guid, PositionState>(state.Positions);
        foreach (var (id, posState) in state.Positions)
        {
            if (posState.Symbol != evt.Symbol) continue;
            var (nextPos, posEffects) = PositionLifecycle.Apply(posState, evt);
            newPositions[id] = nextPos;
            effects.AddRange(posEffects);
        }

        return new EngineDecision(state with { Positions = newPositions }, effects);
    }

    // iter-35 (A2): now WIRED via Kernel.DecideEquity. Folds the authoritative drawdown + account slice
    // (pure, config-agnostic). The config-dependent breach watchdog (enter protection / force-close,
    // toggle-gated) is layered on top in Kernel.DecideEquity — it absorbs AccountProcessor:79-115.
    private static EngineDecision HandleEquityObserved(EngineState state, EquityObserved evt)
    {
        var newDrawdown = DrawdownReducer.Apply(state.Drawdown, evt.Equity);
        var account = new AccountView(evt.Balance, evt.Equity, evt.FloatingPnL);
        return new EngineDecision(state with { Drawdown = newDrawdown, Account = account }, []);
    }

    // iter-35 (A2): WIRED via Kernel.DecideReset. The reducer handles the pure state transition
    // (governor reset + drawdown daily re-base). Protection-exit policy is layered in the kernel.
    private static EngineDecision HandleDayRolled(EngineState state, DayRolled evt)
    {
        var newGovernor = GovernorMachine.ApplyDailyReset(state.Governor);
        var newDrawdown = DrawdownReducer.ApplyDailyReset(state.Drawdown, state.Drawdown.DailyStartEquity);
        return new EngineDecision(state with { Governor = newGovernor, Drawdown = newDrawdown }, []);
    }

    // iter-35 (A2): WIRED via Kernel.DecideReset. Pure weekly drawdown re-base.
    private static EngineDecision HandleWeekRolled(EngineState state, WeekRolled evt)
    {
        var newDrawdown = DrawdownReducer.ApplyWeeklyReset(state.Drawdown, state.Drawdown.WeeklyStartEquity);
        return new EngineDecision(state with { Drawdown = newDrawdown }, []);
    }

    // iter-35 (A2): WIRED via Kernel.DecideReset. Pure monthly drawdown re-base.
    private static EngineDecision HandleMonthRolled(EngineState state, MonthRolled evt)
    {
        // F10 (iter-26): mirror WeekRolled — zero the monthly DD against the existing monthly baseline.
        // Passing DailyStartEquity here re-based the month to the day's start (wrong). The reducer has
        // no current-equity input; the authoritative reset is RiskManager.OnMonthlyReset(currentEquity).
        var newDrawdown = DrawdownReducer.ApplyMonthlyReset(state.Drawdown, state.Drawdown.MonthlyStartEquity);
        return new EngineDecision(state with { Drawdown = newDrawdown }, []);
    }

    private static PositionState? FindPositionByOrderId(EngineState state, Guid orderId)
    {
        return state.Positions.Values.FirstOrDefault(p => p.OrderId == orderId);
    }

    private static EngineDecision HandleForceCloseAll(EngineState state, ForceCloseAllRequested evt)
    {
        var effects = new List<EngineEffect>();
        foreach (var (_, ps) in state.Positions)
        {
            // Close by the venue order id (D1) so the flatten actually reaches the venue.
            effects.Add(new CloseOpenPosition(ps.OrderId, evt.Reason));
        }
        return new EngineDecision(state, effects);
    }
}

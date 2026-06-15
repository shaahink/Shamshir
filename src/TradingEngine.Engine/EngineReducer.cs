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
            evt.Lots, evt.LimitPrice, new Price(0), null, evt.StrategyId);

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

    private static EngineDecision HandleBarClosed(EngineState state, BarClosed evt, List<EngineEffect> effects)
    {
        var newPositions = new Dictionary<Guid, PositionState>(state.Positions);
        foreach (var (id, posState) in state.Positions)
        {
            if (posState.Symbol != evt.Symbol) continue;
            var (nextPos, posEffects) = PositionLifecycle.Apply(posState, evt);
            newPositions[id] = nextPos;
            effects.AddRange(posEffects);

            if (nextPos.Phase is PositionPhase.Open or PositionPhase.Reducing)
            {
                var exit = DetectSlTpExit(nextPos, evt);
                if (exit is not null)
                {
                    effects.Add(new CloseOpenPosition(nextPos.PositionId, exit));
                }
            }
        }

        var newGovernor = GovernorMachine.ApplyBar(state.Governor);

        return new EngineDecision(state with { Positions = newPositions, Governor = newGovernor }, effects);
    }

    private static string? DetectSlTpExit(PositionState state, BarClosed bar)
    {
        if (state.Direction == TradeDirection.Long)
        {
            if (bar.Low <= state.CurrentStopLoss.Value) return "SL";
            if (state.TakeProfit is not null && bar.High >= state.TakeProfit.Value.Value) return "TP";
        }
        else
        {
            if (bar.High >= state.CurrentStopLoss.Value) return "SL";
            if (state.TakeProfit is not null && bar.Low <= state.TakeProfit.Value.Value) return "TP";
        }
        return null;
    }

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

    private static EngineDecision HandleEquityObserved(EngineState state, EquityObserved evt)
    {
        var newDrawdown = DrawdownReducer.Apply(state.Drawdown, evt.Equity);
        return new EngineDecision(state with { Drawdown = newDrawdown }, []);
    }

    private static EngineDecision HandleDayRolled(EngineState state, DayRolled evt)
    {
        var newGovernor = GovernorMachine.ApplyDailyReset(state.Governor);
        var newDrawdown = DrawdownReducer.ApplyDailyReset(state.Drawdown, state.Drawdown.DailyStartEquity);
        return new EngineDecision(state with { Governor = newGovernor, Drawdown = newDrawdown }, []);
    }

    private static EngineDecision HandleWeekRolled(EngineState state, WeekRolled evt)
    {
        var newDrawdown = DrawdownReducer.ApplyWeeklyReset(state.Drawdown, state.Drawdown.WeeklyStartEquity);
        return new EngineDecision(state with { Drawdown = newDrawdown }, []);
    }

    private static PositionState? FindPositionByOrderId(EngineState state, Guid orderId)
    {
        return state.Positions.Values.FirstOrDefault(p => p.OrderId == orderId);
    }

    private static EngineDecision HandleForceCloseAll(EngineState state, ForceCloseAllRequested evt)
    {
        var effects = new List<EngineEffect>();
        foreach (var (posId, _) in state.Positions)
        {
            effects.Add(new CloseOpenPosition(posId, evt.Reason));
        }
        return new EngineDecision(state, effects);
    }
}

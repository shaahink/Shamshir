namespace TradingEngine.Engine;

public static class PositionLifecycle
{
    public static (PositionState State, IReadOnlyList<EngineEffect> Effects) Apply(
        PositionState state, EngineEvent evt)
    {
        return (state.Phase, evt) switch
        {
            (PositionPhase.Intended, OrderSubmitted submitted) => HandleIntendedSubmitted(state, submitted),
            (PositionPhase.Intended, OrderRejected rejected) => HandleRejected(state, rejected),
            (PositionPhase.Submitted, OrderFilled filled) => HandleSubmittedFilled(state, filled),
            (PositionPhase.Submitted, OrderPartiallyFilled partial) => HandleSubmittedPartialFill(state, partial),
            (PositionPhase.Submitted, OrderRejected rejected) => HandleRejected(state, rejected),
            (PositionPhase.Open, OrderFilled filled) => HandleOpenFilled(state, filled),
            (PositionPhase.Open, CloseRequested close) => HandleCloseRequested(state, close),
            (PositionPhase.Open, BarClosed bar) => HandleOpenBar(state, bar),
            (PositionPhase.Open, TickReceived tick) => HandleOpenTick(state, tick),
            (PositionPhase.Reducing, OrderFilled filled) => HandleReducingFilled(state, filled),
            (PositionPhase.Closing, OrderFilled filled) => HandleClosingFilled(state, filled),
            (PositionPhase.Closed, _) => (state, []),
            (PositionPhase.Rejected, _) => (state, []),
            _ => (state, [new RecordDecisionEvent(new DecisionRecord(
                "", evt.OccurredAtUtc, 0, null, null,
                state.Phase.ToString(), "IllegalTransition", null, state.Phase.ToString(),
                $"Illegal transition: {state.Phase} x {evt.GetType().Name}", "{}"))])
        };
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleIntendedSubmitted(
        PositionState state, OrderSubmitted evt)
    {
        var newState = state with { Phase = PositionPhase.Submitted };
        return (newState, []);
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleRejected(
        PositionState state, OrderRejected evt)
    {
        var newState = state with
        {
            Phase = PositionPhase.Rejected,
            RejectionReason = evt.Reason
        };
        return (newState, []);
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleSubmittedFilled(
        PositionState state, OrderFilled evt)
    {
        var newFilled = state.FilledLots + evt.FilledLots;

        if (newFilled >= state.Lots)
        {
            var open = state with
            {
                Phase = PositionPhase.Open,
                FilledLots = state.Lots,
                EntryPrice = state.EntryPrice
            };
            return (open, []);
        }

        var partial = state with
        {
            FilledLots = newFilled,
            Phase = PositionPhase.Submitted
        };
        return (partial, []);
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleSubmittedPartialFill(
        PositionState state, OrderPartiallyFilled evt)
    {
        return HandleSubmittedFilled(state, new OrderFilled(evt.OrderId, evt.Symbol, evt.FilledLots, evt.FillPrice, evt.OccurredAtUtc));
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleOpenFilled(
        PositionState state, OrderFilled evt)
    {
        if (evt.FilledLots > 0 && evt.FilledLots < state.Lots - 0.0001m)
        {
            var reducing = state with
            {
                Phase = PositionPhase.Reducing,
                Lots = state.Lots - evt.FilledLots
            };
            return (reducing, []);
        }

        var closed = state with { Phase = PositionPhase.Closed };
        return (closed, []);
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleCloseRequested(
        PositionState state, CloseRequested evt)
    {
        var closing = state with { Phase = PositionPhase.Closing };
        var effects = new List<EngineEffect>
        {
            new CloseOpenPosition(state.PositionId, evt.Reason)
        };
        return (closing, effects);
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleOpenBar(
        PositionState state, BarClosed evt)
    {
        return (state, []);
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleOpenTick(
        PositionState state, TickReceived evt)
    {
        return (state, []);
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleReducingFilled(
        PositionState state, OrderFilled evt)
    {
        if (evt.FilledLots > 0 && evt.FilledLots < state.Lots - 0.0001m)
        {
            var stillReducing = state with { Lots = state.Lots - evt.FilledLots };
            return (stillReducing, []);
        }

        var closed = state with { Phase = PositionPhase.Closed, Lots = 0 };
        return (closed, []);
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleClosingFilled(
        PositionState state, OrderFilled evt)
    {
        if (evt.FilledLots > 0 && evt.FilledLots < state.Lots - 0.0001m)
        {
            var partialClose = state with
            {
                Phase = PositionPhase.Reducing,
                Lots = state.Lots - evt.FilledLots
            };
            return (partialClose, []);
        }

        var closed = state with { Phase = PositionPhase.Closed, Lots = 0 };
        return (closed, []);
    }

    public static PositionState CreateIntended(
        Guid orderId, Symbol symbol, TradeDirection direction,
        decimal lots, Price? limitPrice, Price stopLoss, Price? takeProfit, string strategyId)
    {
        return new PositionState(
            Guid.NewGuid(), orderId, symbol, direction, lots,
            limitPrice ?? stopLoss, stopLoss, takeProfit,
            DateTime.MinValue, strategyId, PositionPhase.Intended);
    }
}

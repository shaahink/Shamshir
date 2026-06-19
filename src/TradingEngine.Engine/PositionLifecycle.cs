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
            (PositionPhase.Intended, OrderCancelled cancelled) => HandleCancelled(state, cancelled),
            (PositionPhase.Submitted, OrderFilled filled) => HandleSubmittedFilled(state, filled),
            (PositionPhase.Submitted, OrderPartiallyFilled partial) => HandleSubmittedPartialFill(state, partial),
            (PositionPhase.Submitted, OrderRejected rejected) => HandleRejected(state, rejected),
            (PositionPhase.Submitted, OrderCancelled cancelled) => HandleCancelled(state, cancelled),
            (PositionPhase.Open, OrderFilled filled) => HandleOpenFilled(state, filled),
            (PositionPhase.Open, CloseRequested close) => HandleCloseRequested(state, close),
            (PositionPhase.Open, BarClosed bar) => HandleOpenBar(state, bar),
            (PositionPhase.Open, TickReceived tick) => HandleOpenTick(state, tick),
            (PositionPhase.Reducing, OrderFilled filled) => HandleReducingFilled(state, filled),
            (PositionPhase.Closing, OrderFilled filled) => HandleClosingFilled(state, filled),
            (PositionPhase.Closed, _) => (state, []),
            (PositionPhase.Rejected, _) => (state, []),
            (PositionPhase.Cancelled, _) => (state, []),
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
        var effects = new List<EngineEffect>
        {
            Record(state, evt, newState, "Accepted")
        };
        return (newState, effects);
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleRejected(
        PositionState state, OrderRejected evt)
    {
        var newState = state with
        {
            Phase = PositionPhase.Rejected,
            RejectionReason = evt.Reason
        };
        var effects = new List<EngineEffect>
        {
            Record(state, evt, newState, evt.Reason)
        };
        return (newState, effects);
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleCancelled(
        PositionState state, OrderCancelled evt)
    {
        // A resting entry (limit) expired/was cancelled before filling. Terminate the position in a
        // dedicated Cancelled phase and journal WHY — the reason (e.g. ENTRY_EXPIRED) flows through the
        // normalizer so the journal shows the expiry rather than a phantom zero-lot fill.
        var newState = state with
        {
            Phase = PositionPhase.Cancelled,
            RejectionReason = evt.Reason
        };
        var effects = new List<EngineEffect>
        {
            Record(state, evt, newState, evt.Reason)
        };
        return (newState, effects);
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
                EntryPrice = evt.FillPrice,
                OpenedAtUtc = evt.OccurredAtUtc
            };
            var effects = new List<EngineEffect>
            {
                Record(state, evt, open, "Filled"),
                new RegisterRisk(open.PositionId, open.StrategyId, 0)
            };
            return (open, effects);
        }

        var partial = state with
        {
            FilledLots = newFilled,
            Phase = PositionPhase.Submitted
        };
        var partialEffects = new List<EngineEffect>
        {
            Record(state, evt, partial, "PartialFill")
        };
        return (partial, partialEffects);
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
            var reducingEffects = new List<EngineEffect>
            {
                Record(state, evt, reducing, "PartialClose")
            };
            return (reducing, reducingEffects);
        }

        var exitReason = state.CloseReason ?? "FORCE";
        var closed = state with { Phase = PositionPhase.Closed, CloseReason = exitReason };
        var effects = new List<EngineEffect>
        {
            Record(state, evt, closed, exitReason),
            new DeregisterRisk(closed.PositionId),
            new PublishTradeClosed(closed.PositionId, closed.Symbol, closed.Direction, closed.Lots,
                closed.EntryPrice, evt.FillPrice, closed.CurrentStopLoss, closed.TakeProfit,
                closed.StrategyId, exitReason, evt.OccurredAtUtc, closed.OpenedAtUtc,
                OrderId: closed.OrderId, HighWater: closed.HighWater, LowWater: closed.LowWater)
        };
        return (closed, effects);
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleCloseRequested(
        PositionState state, CloseRequested evt)
    {
        var closing = state with { Phase = PositionPhase.Closing, CloseReason = evt.Reason };
        var effects = new List<EngineEffect>
        {
            Record(state, evt, closing, evt.Reason),
            new CloseOpenPosition(state.OrderId, evt.Reason)
        };
        return (closing, effects);
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleOpenBar(
        PositionState state, BarClosed evt)
    {
        var highWater = state.HighWater == 0 ? evt.Close : Math.Max(state.HighWater, evt.High);
        var lowWater = state.LowWater == 0 ? evt.Close : Math.Min(state.LowWater, evt.Low);
        var next = state with { HighWater = highWater, LowWater = lowWater };
        var effects = new List<EngineEffect>
        {
            Record(state, evt, next, "BarUpdate")
        };
        return (next, effects);
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleOpenTick(
        PositionState state, TickReceived evt)
    {
        var highWater = state.HighWater == 0 ? evt.Bid : Math.Max(state.HighWater, evt.Bid);
        var lowWater = state.LowWater == 0 ? evt.Ask : Math.Min(state.LowWater, evt.Ask);
        var next = state with { HighWater = highWater, LowWater = lowWater };
        var effects = new List<EngineEffect>
        {
            Record(state, evt, next, "TickUpdate")
        };
        return (next, effects);
    }

    private static (PositionState, IReadOnlyList<EngineEffect>) HandleReducingFilled(
        PositionState state, OrderFilled evt)
    {
        if (evt.FilledLots > 0 && evt.FilledLots < state.Lots - 0.0001m)
        {
            var stillReducing = state with { Lots = state.Lots - evt.FilledLots };
            var stillEffects = new List<EngineEffect>
            {
                Record(state, evt, stillReducing, "StillReducing")
            };
            return (stillReducing, stillEffects);
        }

        var exitReason = state.CloseReason ?? "FORCE";
        var closed = state with { Phase = PositionPhase.Closed, Lots = 0, CloseReason = exitReason };
        var effects = new List<EngineEffect>
        {
            Record(state, evt, closed, exitReason),
            new DeregisterRisk(closed.PositionId),
            new PublishTradeClosed(closed.PositionId, closed.Symbol, closed.Direction, state.Lots,
                closed.EntryPrice, evt.FillPrice, closed.CurrentStopLoss, closed.TakeProfit,
                closed.StrategyId, exitReason, evt.OccurredAtUtc, closed.OpenedAtUtc,
                OrderId: closed.OrderId, HighWater: closed.HighWater, LowWater: closed.LowWater)
        };
        return (closed, effects);
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
            var partialEffects = new List<EngineEffect>
            {
                Record(state, evt, partialClose, "PartialCloseWhileClosing")
            };
            return (partialClose, partialEffects);
        }

        var exitReason = state.CloseReason ?? "FORCE";
        var closed = state with { Phase = PositionPhase.Closed, Lots = 0, CloseReason = exitReason };
        var effects = new List<EngineEffect>
        {
            Record(state, evt, closed, exitReason),
            new DeregisterRisk(closed.PositionId),
            new PublishTradeClosed(closed.PositionId, closed.Symbol, closed.Direction, state.Lots,
                closed.EntryPrice, evt.FillPrice, closed.CurrentStopLoss, closed.TakeProfit,
                closed.StrategyId, exitReason, evt.OccurredAtUtc, closed.OpenedAtUtc,
                OrderId: closed.OrderId, HighWater: closed.HighWater, LowWater: closed.LowWater)
        };
        return (closed, effects);
    }

    public static PositionState CreateIntended(
        Guid orderId, Symbol symbol, TradeDirection direction,
        decimal lots, Price? limitPrice, Price stopLoss, Price? takeProfit, string strategyId)
    {
        return new PositionState(
            orderId, orderId, symbol, direction, lots,
            limitPrice ?? stopLoss, stopLoss, takeProfit,
            DateTime.MinValue, strategyId, PositionPhase.Intended);
    }

    public static Price? TrailStepPips(PositionState state, decimal bid, decimal ask, Pips stepPips, SymbolInfo symbol)
    {
        var step = (decimal)stepPips.Value * symbol.PipSize;
        if (state.Direction == TradeDirection.Long)
        {
            var newSl = bid - step;
            return newSl > state.CurrentStopLoss.Value ? new Price(RoundToTick(newSl, symbol.TickSize)) : null;
        }
        else
        {
            var newSl = ask + step;
            return newSl < state.CurrentStopLoss.Value ? new Price(RoundToTick(newSl, symbol.TickSize)) : null;
        }
    }

    public static Price? TrailAtr(PositionState state, decimal highWater, decimal lowWater, double atr, double multiplier, SymbolInfo symbol)
    {
        var offset = (decimal)(atr * multiplier);
        if (state.Direction == TradeDirection.Long)
        {
            var newSl = highWater - offset;
            return newSl > state.CurrentStopLoss.Value ? new Price(RoundToTick(newSl, symbol.TickSize)) : null;
        }
        else
        {
            var newSl = lowWater + offset;
            return newSl < state.CurrentStopLoss.Value ? new Price(RoundToTick(newSl, symbol.TickSize)) : null;
        }
    }

    public static Price? TryBreakeven(PositionState state, decimal bid, decimal ask, double triggerR, Pips bufferPips, SymbolInfo symbol)
    {
        var slDistance = Math.Abs(state.EntryPrice.Value - state.CurrentStopLoss.Value);
        var triggerDistance = slDistance * (decimal)triggerR;
        var buffer = (decimal)bufferPips.Value * symbol.PipSize;
        if (state.Direction == TradeDirection.Long)
        {
            var inProfit = bid - state.EntryPrice.Value;
            if (inProfit < triggerDistance) return null;
            var beSl = state.EntryPrice.Value + buffer;
            return beSl > state.CurrentStopLoss.Value ? new Price(RoundToTick(beSl, symbol.TickSize)) : null;
        }
        else
        {
            var inProfit = state.EntryPrice.Value - ask;
            if (inProfit < triggerDistance) return null;
            var beSl = state.EntryPrice.Value - buffer;
            return beSl < state.CurrentStopLoss.Value ? new Price(RoundToTick(beSl, symbol.TickSize)) : null;
        }
    }

    public static Price? TrailStructure(PositionState state, IReadOnlyList<Bar> recentBars, int lookback, double atr, double multiplier, SymbolInfo symbol)
    {
        var offset = (decimal)(atr * multiplier);
        var window = recentBars.TakeLast(Math.Min(lookback + 2, recentBars.Count)).ToList();
        if (window.Count < 3) return null;
        decimal? swingLevel = null;
        if (state.Direction == TradeDirection.Long)
        {
            for (var i = window.Count - 2; i >= 1; i--)
            {
                if (window[i].Low < window[i - 1].Low && window[i].Low < window[i + 1].Low)
                { swingLevel = window[i].Low; break; }
            }
            if (swingLevel.HasValue)
            {
                var newSl = RoundToTick(swingLevel.Value - offset, symbol.TickSize);
                return newSl > state.CurrentStopLoss.Value ? new Price(newSl) : null;
            }
        }
        else
        {
            for (var i = window.Count - 2; i >= 1; i--)
            {
                if (window[i].High > window[i - 1].High && window[i].High > window[i + 1].High)
                { swingLevel = window[i].High; break; }
            }
            if (swingLevel.HasValue)
            {
                var newSl = RoundToTick(swingLevel.Value + offset, symbol.TickSize);
                return newSl < state.CurrentStopLoss.Value ? new Price(newSl) : null;
            }
        }
        return null;
    }

    public static Price? TrailSteppedR(PositionState state, decimal bid, decimal ask, double[] rLevels, SymbolInfo symbol)
    {
        var initialSl = state.InitialSlDistance > 0
            ? state.InitialSlDistance
            : Math.Abs(state.EntryPrice.Value - state.CurrentStopLoss.Value);
        if (state.Direction == TradeDirection.Long)
        {
            var profit = bid - state.EntryPrice.Value;
            for (var i = rLevels.Length - 1; i >= 0; i--)
            {
                if (profit >= initialSl * (decimal)rLevels[i])
                {
                    var newSl = i == 0 ? state.EntryPrice.Value : state.EntryPrice.Value + initialSl * (decimal)rLevels[i - 1];
                    newSl = RoundToTick(newSl, symbol.TickSize);
                    return newSl > state.CurrentStopLoss.Value ? new Price(newSl) : null;
                }
            }
        }
        else
        {
            var profit = state.EntryPrice.Value - ask;
            for (var i = rLevels.Length - 1; i >= 0; i--)
            {
                if (profit >= initialSl * (decimal)rLevels[i])
                {
                    var newSl = i == 0 ? state.EntryPrice.Value : state.EntryPrice.Value - initialSl * (decimal)rLevels[i - 1];
                    newSl = RoundToTick(newSl, symbol.TickSize);
                    return newSl < state.CurrentStopLoss.Value ? new Price(newSl) : null;
                }
            }
        }
        return null;
    }

    private static decimal RoundToTick(decimal price, decimal tickSize)
        => Math.Round(price / tickSize) * tickSize;

    private static RecordDecisionEvent Record(PositionState before, EngineEvent evt, PositionState after, string reason)
        => new(new DecisionRecord("", evt.OccurredAtUtc, 0, before.Symbol.Value, before.StrategyId,
            before.Phase.ToString(), evt.GetType().Name, null, after.Phase.ToString(), reason, "{}"));
}

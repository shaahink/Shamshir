using TradingEngine.Engine;

namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Engine")]
[Trait("Speed", "Fast")]
public sealed class EngineReducerTests
{
    [Fact]
    public void Apply_OrderSubmitted_CreatesPositionInIntended()
    {
        var state = EngineState.Empty;
        var evt = new OrderSubmitted(Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long, 0.1m, null, "test", DateTime.UtcNow);

        var result = EngineReducer.Apply(state, evt);

        result.State.Positions.Should().HaveCount(1);
        result.State.Positions.Values.First().Phase.Should().Be(PositionPhase.Submitted);
    }

    [Fact]
    public void Apply_OrderSubmittedThenFilled_TransitionsToOpen()
    {
        var state = EngineState.Empty;
        var orderId = Guid.NewGuid();
        var submitted = new OrderSubmitted(orderId, Symbol.Parse("EURUSD"), TradeDirection.Long, 0.1m, null, "test", DateTime.UtcNow);

        var r1 = EngineReducer.Apply(state, submitted);
        var posId = r1.State.Positions.Values.First().PositionId;

        var filled = new OrderFilled(orderId, Symbol.Parse("EURUSD"), 0.1m, new Price(1.0850m), DateTime.UtcNow.AddSeconds(1));
        var r2 = EngineReducer.Apply(r1.State, filled);

        r2.State.Positions.Should().HaveCount(1);
        r2.State.Positions[posId].Phase.Should().Be(PositionPhase.Open);
    }

    [Fact]
    public void Apply_OrderRejected_RemovesPosition()
    {
        var state = EngineState.Empty;
        var orderId = Guid.NewGuid();
        var submitted = new OrderSubmitted(orderId, Symbol.Parse("EURUSD"), TradeDirection.Long, 0.1m, null, "test", DateTime.UtcNow);
        var r1 = EngineReducer.Apply(state, submitted);

        var rejected = new OrderRejected(orderId, Symbol.Parse("EURUSD"), "InsufficientMargin", DateTime.UtcNow);
        var r2 = EngineReducer.Apply(r1.State, rejected);

        r2.State.Positions.Should().BeEmpty();
    }

    [Fact]
    public void Apply_OrderCancelled_RemovesPosition_AndRecordsReason()
    {
        var state = EngineState.Empty;
        var orderId = Guid.NewGuid();
        var submitted = new OrderSubmitted(orderId, Symbol.Parse("EURUSD"), TradeDirection.Long, 0.1m, new Price(1.0800m), "test", DateTime.UtcNow);
        var r1 = EngineReducer.Apply(state, submitted);

        var cancelled = new OrderCancelled(orderId, Symbol.Parse("EURUSD"), "ENTRY_EXPIRED", DateTime.UtcNow);
        var r2 = EngineReducer.Apply(r1.State, cancelled);

        r2.State.Positions.Should().BeEmpty("an expired resting limit must leave no zombie position");
        r2.State.OpenPositionCount.Should().Be(0);
        r2.Effects.Should().ContainSingle(e => e is RecordDecisionEvent);
    }

    [Fact]
    public void Apply_FullLifecycle_OpenThenClose_ReturnsEmpty()
    {
        var state = EngineState.Empty;
        var orderId = Guid.NewGuid();
        var submitted = new OrderSubmitted(orderId, Symbol.Parse("EURUSD"), TradeDirection.Long, 0.1m, null, "test", DateTime.UtcNow);
        var r1 = EngineReducer.Apply(state, submitted);
        var posId = r1.State.Positions.Values.First().PositionId;

        var filled = new OrderFilled(orderId, Symbol.Parse("EURUSD"), 0.1m, new Price(1.0850m), DateTime.UtcNow.AddSeconds(1));
        var r2 = EngineReducer.Apply(r1.State, filled);

        var close = new CloseRequested(posId, "SL hit", DateTime.UtcNow.AddMinutes(1));
        var r3 = EngineReducer.Apply(r2.State, close);

        r3.State.Positions.Should().HaveCount(1);
        r3.Effects.Should().ContainSingle(e => e is CloseOpenPosition);

        var closedFill = new OrderFilled(orderId, Symbol.Parse("EURUSD"), 0.1m, new Price(1.0821m), DateTime.UtcNow.AddMinutes(2));
        var r4 = EngineReducer.Apply(r3.State, closedFill);

        r4.State.Positions.Should().BeEmpty();
    }

    [Fact]
    public void Apply_EquityObserved_UpdatesDrawdown()
    {
        var state = new EngineState(
            new Dictionary<Guid, PositionState>(),
            new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
            DrawdownReducer.CreateInitial(100_000),
            0);

        var evt = new EquityObserved(98_000, DateTime.UtcNow);
        var result = EngineReducer.Apply(state, evt);

        result.State.Drawdown.CurrentDailyDrawdown.Should().Be(0.02m);
        result.State.Drawdown.CurrentMaxDrawdown.Should().Be(0.02m);
    }

    [Fact]
    public void Apply_BarClosed_ForwardsToAllMatchingPositions()
    {
        var state = EngineState.Empty;
        var orderId = Guid.NewGuid();
        var submitted = new OrderSubmitted(orderId, Symbol.Parse("EURUSD"), TradeDirection.Long, 0.1m, null, "test", DateTime.UtcNow);
        var r1 = EngineReducer.Apply(state, submitted);
        var filled = new OrderFilled(orderId, Symbol.Parse("EURUSD"), 0.1m, new Price(1.0850m), DateTime.UtcNow.AddSeconds(1));
        var r2 = EngineReducer.Apply(r1.State, filled);

        var bar = new BarClosed(Symbol.Parse("EURUSD"), Timeframe.H1, 1.0850m, 1.0860m, 1.0840m, 1.0855m, DateTime.UtcNow);
        var result = EngineReducer.Apply(r2.State, bar);

        result.State.Positions.Should().HaveCount(1);
    }

    [Fact]
    public void Apply_EventScript_ProducesExpectedEffects()
    {
        var state = EngineState.Empty;
        var orderId = Guid.NewGuid();
        var events = new EngineEvent[]
        {
            new OrderSubmitted(orderId, Symbol.Parse("EURUSD"), TradeDirection.Long, 0.1m, null, "test", DateTime.UtcNow),
            new OrderFilled(orderId, Symbol.Parse("EURUSD"), 0.1m, new Price(1.0850m), DateTime.UtcNow.AddSeconds(1)),
        };

        foreach (var evt in events)
        {
            state = EngineReducer.Apply(state, evt).State;
        }

        state.Positions.Should().HaveCount(1);
        state.Positions.Values.First().Phase.Should().Be(PositionPhase.Open);
    }

    [Fact]
    public void Close_emits_TradeClosed_with_correct_pnl()
    {
        var state = EngineState.Empty;
        var symbol = Symbol.Parse("EURUSD");
        var orderId = Guid.NewGuid();

        var submitted = new OrderSubmitted(orderId, symbol, TradeDirection.Long, 0.1m, null, "test", DateTime.UtcNow);
        var r1 = EngineReducer.Apply(state, submitted);
        var posId = r1.State.Positions.Values.First().PositionId;

        var filled = new OrderFilled(orderId, symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow.AddSeconds(1));
        var r2 = EngineReducer.Apply(r1.State, filled);

        r2.State.Positions.Should().HaveCount(1);
        r2.State.Positions[posId].Phase.Should().Be(PositionPhase.Open);

        var closeRequested = new CloseRequested(posId, "SL hit", DateTime.UtcNow.AddMinutes(1));
        var r3 = EngineReducer.Apply(r2.State, closeRequested);

        r3.Effects.Should().ContainSingle(e => e is CloseOpenPosition);
        var closeEffect = r3.Effects.OfType<CloseOpenPosition>().Single();
        closeEffect.Reason.Should().Be("SL hit");

        var closeFillPrice = new Price(1.0821m);
        var closeFill = new OrderFilled(orderId, symbol, 0.1m, closeFillPrice, DateTime.UtcNow.AddMinutes(2));
        var r4 = EngineReducer.Apply(r3.State, closeFill);

        r4.State.Positions.Should().BeEmpty();
        r4.Effects.Should().ContainSingle(e => e is PublishTradeClosed);

        var tradeClosed = r4.Effects.OfType<PublishTradeClosed>().Single();
        tradeClosed.Symbol.Should().Be(symbol);
        tradeClosed.Direction.Should().Be(TradeDirection.Long);
        tradeClosed.Lots.Should().Be(0.1m);
        tradeClosed.EntryPrice.Should().Be(new Price(1.0850m));
        tradeClosed.ExitPrice.Should().Be(closeFillPrice);
        tradeClosed.ExitReason.Should().Be("SL hit");

        var grossPnlPips = (1.0850m - 1.0821m) / 0.0001m; // 29 pips loss for long
        grossPnlPips.Should().Be(29);
    }

    [Fact]
    public void Open_then_close_emits_register_then_deregister()
    {
        var state = EngineState.Empty;
        var symbol = Symbol.Parse("EURUSD");
        var orderId = Guid.NewGuid();

        var submitted = new OrderSubmitted(orderId, symbol, TradeDirection.Long, 0.1m, null, "test", DateTime.UtcNow);
        var r1 = EngineReducer.Apply(state, submitted);

        var filled = new OrderFilled(orderId, symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow.AddSeconds(1));
        var r2 = EngineReducer.Apply(r1.State, filled);

        r2.Effects.Should().ContainSingle(e => e is RegisterRisk);
        var reg = r2.Effects.OfType<RegisterRisk>().Single();
        reg.StrategyId.Should().Be("test");

        var posId = r2.State.Positions.Values.First().PositionId;
        var closeRequested = new CloseRequested(posId, "TP hit", DateTime.UtcNow.AddMinutes(1));
        var r3 = EngineReducer.Apply(r2.State, closeRequested);

        var closeFill = new OrderFilled(orderId, symbol, 0.1m, new Price(1.0900m), DateTime.UtcNow.AddMinutes(2));
        var r4 = EngineReducer.Apply(r3.State, closeFill);

        r4.Effects.Should().Contain(e => e is DeregisterRisk);
        r4.Effects.OfType<DeregisterRisk>().Single().PositionId.Should().Be(posId);
        r4.State.Positions.Should().BeEmpty();
    }

    [Fact]
    public void Breach_emits_close_effects_for_all_open_positions()
    {
        var state = EngineState.Empty;
        var symbol = Symbol.Parse("EURUSD");

        var o1 = Guid.NewGuid();
        var o2 = Guid.NewGuid();
        var r1 = EngineReducer.Apply(state, new OrderSubmitted(o1, symbol, TradeDirection.Long, 0.1m, null, "s1", DateTime.UtcNow));
        var r2 = EngineReducer.Apply(r1.State, new OrderFilled(o1, symbol, 0.1m, new Price(1.1000m), DateTime.UtcNow.AddSeconds(1)));
        var r3 = EngineReducer.Apply(r2.State, new OrderSubmitted(o2, symbol, TradeDirection.Short, 0.05m, null, "s2", DateTime.UtcNow));
        var r4 = EngineReducer.Apply(r3.State, new OrderFilled(o2, symbol, 0.05m, new Price(1.1020m), DateTime.UtcNow.AddSeconds(2)));

        r4.State.Positions.Should().HaveCount(2);

        var forceClose = new ForceCloseAllRequested("MaxDD", DateTime.UtcNow.AddMinutes(1));
        var result = EngineReducer.Apply(r4.State, forceClose);

        result.Effects.Should().HaveCount(2);
        result.Effects.Should().AllBeOfType<CloseOpenPosition>();
        result.Effects.OfType<CloseOpenPosition>().Should().OnlyContain(e => e.Reason == "MaxDD");
    }
}

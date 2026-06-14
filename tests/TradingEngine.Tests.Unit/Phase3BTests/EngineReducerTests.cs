using TradingEngine.Engine;

namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Engine")]
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
}

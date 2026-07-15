using TradingEngine.Engine;

namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Engine")]
[Trait("Speed", "Fast")]
public sealed class PositionLifecycleTests
{
    private static PositionState CreateIntended() => PositionLifecycle.CreateIntended(
        Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long,
        0.1m, new Price(1.0850m), new Price(1.0821m), new Price(1.0880m), "test");

    // --- Intended phase ---

    [Fact]
    public void Intended_OrderSubmitted_TransitionsToSubmitted()
    {
        var state = CreateIntended();
        var evt = new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow);

        var (next, effects) = PositionLifecycle.Apply(state, evt);

        next.Phase.Should().Be(PositionPhase.Submitted);
        effects.Should().Contain(e => e is RecordDecisionEvent);
    }

    [Fact]
    public void Intended_OrderRejected_TransitionsToRejected()
    {
        var state = CreateIntended();
        var evt = new OrderRejected(state.OrderId, state.Symbol, "InsufficientMargin", DateTime.UtcNow);

        var (next, effects) = PositionLifecycle.Apply(state, evt);

        next.Phase.Should().Be(PositionPhase.Rejected);
        next.RejectionReason.Should().Be("InsufficientMargin");
    }

    [Fact]
    public void Submitted_OrderCancelled_TransitionsToCancelled_NotPhantomFill()
    {
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, new Price(1.0850m), "test", DateTime.UtcNow)).State;

        var evt = new OrderCancelled(state.OrderId, state.Symbol, "ENTRY_EXPIRED", DateTime.UtcNow);
        var (next, effects) = PositionLifecycle.Apply(state, evt);

        // The old `_ => OrderFilled` default mis-read a cancellation as a zero-lot PartialFill and left
        // the order stuck in Submitted. It must now terminate in Cancelled with the reason recorded.
        next.Phase.Should().Be(PositionPhase.Cancelled);
        next.RejectionReason.Should().Be("ENTRY_EXPIRED");
        effects.Should().ContainSingle(e => e is RecordDecisionEvent);
    }

    [Fact]
    public void Intended_UnrelatedEvent_EmitsIllegalTransition()
    {
        var state = CreateIntended();
        var evt = new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow);

        var (next, effects) = PositionLifecycle.Apply(state, evt);

        next.Phase.Should().Be(PositionPhase.Intended);
        effects.Should().ContainSingle(e => e is RecordDecisionEvent);
    }

    // --- Submitted phase ---

    [Fact]
    public void Submitted_FullFill_TransitionsToOpen()
    {
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow)).State;

        var fill = new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow);
        var (next, effects) = PositionLifecycle.Apply(state, fill);

        next.Phase.Should().Be(PositionPhase.Open);
        next.FilledLots.Should().Be(0.1m);
    }

    [Fact]
    public void Submitted_PartialFill_StaysSubmitted()
    {
        var state = PositionLifecycle.CreateIntended(
            Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long,
            0.2m, new Price(1.0850m), new Price(1.0821m), null, "test");
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.2m, null, "test", DateTime.UtcNow)).State;

        var fill = new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow);
        var (next, effects) = PositionLifecycle.Apply(state, fill);

        next.Phase.Should().Be(PositionPhase.Submitted);
        next.FilledLots.Should().Be(0.1m);
    }

    [Fact]
    public void Submitted_PartialThenRemainderFill_TransitionsToOpen()
    {
        var state = PositionLifecycle.CreateIntended(
            Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long,
            0.2m, new Price(1.0850m), new Price(1.0821m), null, "test");
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.2m, null, "test", DateTime.UtcNow)).State;

        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow)).State;

        var remainder = new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0852m), DateTime.UtcNow.AddMinutes(1));
        var (next, effects) = PositionLifecycle.Apply(state, remainder);

        next.Phase.Should().Be(PositionPhase.Open);
        next.FilledLots.Should().Be(0.2m);
    }

    [Fact]
    public void Submitted_OrderRejected_TransitionsToRejected()
    {
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow)).State;

        var rejected = new OrderRejected(state.OrderId, state.Symbol, "Timeout", DateTime.UtcNow);
        var (next, effects) = PositionLifecycle.Apply(state, rejected);

        next.Phase.Should().Be(PositionPhase.Rejected);
        next.RejectionReason.Should().Be("Timeout");
    }

    [Fact]
    public void Submitted_OrderPartiallyFilled_DelegatesToFilled()
    {
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow)).State;

        var partial = new OrderPartiallyFilled(state.OrderId, state.Symbol, 0.05m, new Price(1.0850m), DateTime.UtcNow);
        var (next, effects) = PositionLifecycle.Apply(state, partial);

        next.Phase.Should().Be(PositionPhase.Submitted);
        next.FilledLots.Should().Be(0.05m);
    }

    // --- Open phase ---

    [Fact]
    public void Open_PartialClose_StaysOpen_AndPublishesPartialTrade()
    {
        // iter-38 A4: a partial close of an OPEN position (PartialTp) keeps the REMAINDER open so it keeps
        // trailing, and publishes the closed portion as a PARTIAL trade. (Reducing is now reached only via
        // the Closing path — a partial fill of a force-close order.)
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow)).State;

        var partialClose = new OrderFilled(state.OrderId, state.Symbol, 0.04m, new Price(1.0860m), DateTime.UtcNow.AddMinutes(1));
        var (next, effects) = PositionLifecycle.Apply(state, partialClose);

        next.Phase.Should().Be(PositionPhase.Open);
        next.Lots.Should().Be(0.06m);

        var trade = effects.OfType<PublishTradeClosed>().Single();
        trade.ExitReason.Should().Be("PARTIAL");
        trade.Lots.Should().Be(0.04m);
    }

    [Fact]
    public void Open_FullClose_TransitionsToClosed()
    {
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow)).State;

        var fullClose = new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0860m), DateTime.UtcNow.AddMinutes(1));
        var (next, effects) = PositionLifecycle.Apply(state, fullClose);

        next.Phase.Should().Be(PositionPhase.Closed);
    }

    [Fact]
    public void CreateIntended_SetsInitialStopLoss()
    {
        var state = CreateIntended();

        state.InitialStopLoss.Should().Be(new Price(1.0821m));
    }

    [Fact]
    public void Open_FullClose_PublishesInitialStopLoss_UnaffectedByLaterStopMove()
    {
        // P0.1 regression: CurrentStopLoss gets moved by breakeven/trailing while the position is Open
        // (EngineReducer.HandleStopLossModify does `ps with { CurrentStopLoss = ... }`). The published
        // trade-closed effect must still carry the ORIGINAL (InitialStopLoss) risk distance untouched,
        // even though CurrentStopLoss (the final stop) has moved to breakeven.
        var state = CreateIntended(); // initial stop 1.0821
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow)).State;

        // Simulate a breakeven move: the reducer would do `ps with { CurrentStopLoss = ... }`.
        state = state with { CurrentStopLoss = new Price(1.0850m) };

        var fullClose = new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0908m), DateTime.UtcNow.AddMinutes(5));
        var (next, effects) = PositionLifecycle.Apply(state, fullClose);

        next.Phase.Should().Be(PositionPhase.Closed);
        var trade = effects.OfType<PublishTradeClosed>().Single();
        trade.InitialStopLoss.Should().Be(new Price(1.0821m));
        trade.StopLoss.Should().Be(new Price(1.0850m)); // final/trailed stop still reported separately
    }

    [Fact]
    public void Open_CloseRequested_TransitionsToClosing_EmitsCloseOpenPosition()
    {
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow)).State;

        var closeReq = new CloseRequested(state.PositionId, "SL hit", DateTime.UtcNow.AddMinutes(1));
        var (next, effects) = PositionLifecycle.Apply(state, closeReq);

        next.Phase.Should().Be(PositionPhase.Closing);
        effects.Should().Contain(e => e is CloseOpenPosition);
        effects.OfType<CloseOpenPosition>().Single().Reason.Should().Be("SL hit");
    }

    [Fact]
    public void Open_BarClosed_StaysOpen()
    {
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow)).State;

        var bar = new BarClosed(state.Symbol, Timeframe.H1, 1.0850m, 1.0860m, 1.0840m, 1.0855m, DateTime.UtcNow);
        var (next, effects) = PositionLifecycle.Apply(state, bar);

        next.Phase.Should().Be(PositionPhase.Open);
    }

    [Fact]
    public void Open_TickReceived_StaysOpen()
    {
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow)).State;

        var tick = new TickReceived(state.Symbol, 1.0852m, 1.0854m, DateTime.UtcNow);
        var (next, effects) = PositionLifecycle.Apply(state, tick);

        next.Phase.Should().Be(PositionPhase.Open);
    }

    // --- Reducing phase ---

    [Fact]
    public void Reducing_PartialFill_StayReducing()
    {
        // Reducing is entered via the Closing path (iter-38 A4): force-close, then a partial fill of the
        // close order reduces while Closing → Reducing. A further partial fill stays Reducing.
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new CloseRequested(state.PositionId, "Manual", DateTime.UtcNow.AddMinutes(1))).State;
        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.04m, new Price(1.0860m), DateTime.UtcNow.AddMinutes(2))).State;

        var more = new OrderFilled(state.OrderId, state.Symbol, 0.02m, new Price(1.0865m), DateTime.UtcNow.AddMinutes(3));
        var (next, effects) = PositionLifecycle.Apply(state, more);

        next.Phase.Should().Be(PositionPhase.Reducing);
        next.Lots.Should().Be(0.04m);
    }

    [Fact]
    public void Reducing_FinalFill_TransitionsToClosed()
    {
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new CloseRequested(state.PositionId, "Manual", DateTime.UtcNow.AddMinutes(1))).State;
        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.04m, new Price(1.0860m), DateTime.UtcNow.AddMinutes(2))).State;

        var final = new OrderFilled(state.OrderId, state.Symbol, 0.06m, new Price(1.0870m), DateTime.UtcNow.AddMinutes(3));
        var (next, effects) = PositionLifecycle.Apply(state, final);

        next.Phase.Should().Be(PositionPhase.Closed);
        next.Lots.Should().Be(0);
    }

    // --- Closing phase ---

    [Fact]
    public void Closing_FullFill_TransitionsToClosed()
    {
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new CloseRequested(state.PositionId, "Manual", DateTime.UtcNow.AddMinutes(1))).State;

        var fill = new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0860m), DateTime.UtcNow.AddMinutes(2));
        var (next, effects) = PositionLifecycle.Apply(state, fill);

        next.Phase.Should().Be(PositionPhase.Closed);
    }

    [Fact]
    public void Closing_PartialFill_GoesToReducing()
    {
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new CloseRequested(state.PositionId, "Manual", DateTime.UtcNow.AddMinutes(1))).State;

        var partial = new OrderFilled(state.OrderId, state.Symbol, 0.04m, new Price(1.0860m), DateTime.UtcNow.AddMinutes(2));
        var (next, effects) = PositionLifecycle.Apply(state, partial);

        next.Phase.Should().Be(PositionPhase.Reducing);
        next.Lots.Should().Be(0.06m);
    }

    // --- Closed / Rejected — terminal states ---

    [Fact]
    public void Closed_AnyEvent_StaysClosed()
    {
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderSubmitted(state.OrderId, state.Symbol, state.Direction, 0.1m, null, "test", DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0850m), DateTime.UtcNow)).State;
        state = PositionLifecycle.Apply(state, new OrderFilled(state.OrderId, state.Symbol, 0.1m, new Price(1.0860m), DateTime.UtcNow.AddMinutes(1))).State;

        var bar = new BarClosed(state.Symbol, Timeframe.H1, 1.0850m, 1.0860m, 1.0840m, 1.0855m, DateTime.UtcNow);
        var (next, effects) = PositionLifecycle.Apply(state, bar);

        next.Phase.Should().Be(PositionPhase.Closed);
        effects.Should().BeEmpty();
    }

    [Fact]
    public void Rejected_AnyEvent_StaysRejected()
    {
        var state = CreateIntended();
        state = PositionLifecycle.Apply(state, new OrderRejected(state.OrderId, state.Symbol, "Margin", DateTime.UtcNow)).State;

        var bar = new BarClosed(state.Symbol, Timeframe.H1, 1.0850m, 1.0860m, 1.0840m, 1.0855m, DateTime.UtcNow);
        var (next, effects) = PositionLifecycle.Apply(state, bar);

        next.Phase.Should().Be(PositionPhase.Rejected);
    }
}

namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Infrastructure")]
public sealed class TypedEventBusTests
{
    [Fact]
    public async Task PublishAsync_CallsSubscribedHandler()
    {
        var bus = new TypedEventBus();
        var handled = false;
        var handler = Substitute.For<IEventHandler<EquityUpdated>>();
        handler.HandleAsync(Arg.Any<EquityUpdated>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => handled = true);

        bus.Subscribe(handler);
        var snapshot = new EquitySnapshot(DateTime.UtcNow, 0, 0, 0, 0, 0, 0, 0, EngineMode.Backtest);
        var riskState = new ExtendedRiskState { TradingAllowed = true };
        await bus.PublishAsync(new EquityUpdated(snapshot, riskState, DateTime.UtcNow), CancellationToken.None);

        handled.Should().BeTrue();
    }

    [Fact]
    public async Task PublishAsync_NoSubscriber_DoesNotThrow()
    {
        var bus = new TypedEventBus();
        var snapshot = new EquitySnapshot(DateTime.UtcNow, 0, 0, 0, 0, 0, 0, 0, EngineMode.Backtest);
        var riskState = new ExtendedRiskState { TradingAllowed = true };
        await bus.Invoking(b => b.PublishAsync(new EquityUpdated(snapshot, riskState, DateTime.UtcNow), CancellationToken.None))
            .Should().NotThrowAsync();
    }
}

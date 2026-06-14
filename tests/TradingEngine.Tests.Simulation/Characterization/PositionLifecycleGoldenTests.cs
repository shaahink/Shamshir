using Microsoft.Extensions.Logging;
using NSubstitute;

namespace TradingEngine.Tests.Simulation.Characterization;

[Trait("Category", "Characterization")]
public sealed class PositionLifecycleGoldenTests
{
    private static ISymbolInfoRegistry CreateRegistry()
    {
        var r = new SymbolInfoRegistry();
        r.Register(new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));
        return r;
    }

    private static (PositionTracker tracker, IPositionManager posMgr, IEventBus eventBus, IRiskManager riskMgr, ITradingGovernor governor) CreateTracker()
    {
        var registry = CreateRegistry();
        var crossRate = new Func<string, string, decimal>((_, _) => 1m);
        var riskMgr = Substitute.For<IRiskManager>();
        var posMgr = new PositionManager(registry, Substitute.For<IIndicatorService>(), Substitute.For<ILogger<PositionManager>>());
        var eventBus = Substitute.For<IEventBus>();
        var runCtx = new EngineRunContext("golden-test");
        var clock = Substitute.For<IEngineClock>();
        clock.UtcNow.Returns(DateTime.UtcNow);
        var logger = Substitute.For<ILogger<PositionTracker>>();
        var governor = Substitute.For<ITradingGovernor>();

        var tracker = new PositionTracker(registry, crossRate, riskMgr, posMgr, eventBus, runCtx, clock, logger, governor);
        return (tracker, posMgr, eventBus, riskMgr, governor);
    }

    [Fact]
    public async Task FullFill_CreatesPosition()
    {
        var (tracker, _, eventBus, riskMgr, _) = CreateTracker();

        var intent = new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
            new Price(1.0850m), new Price(1.0821m), "test", "standard", "", DateTime.UtcNow);
        var orderReq = new OrderRequest(intent, 0.1m, intent.Symbol, intent.Direction, OrderType.Market, intent.LimitPrice);

        var orderId = Guid.NewGuid();
        tracker.TrackOrder(orderId, orderReq, 28m);

        var evt = new ExecutionEvent(orderId, OrderState.Filled, new Price(1.0850m), 0.1m, null, DateTime.UtcNow);
        await tracker.OnExecutionAsync(evt, []);

        tracker.OpenPositions.Should().HaveCount(1);
        var pos = tracker.OpenPositions.Values.First();
        pos.Symbol.Value.Should().Be("EURUSD");
        pos.Direction.Should().Be(TradeDirection.Long);
        pos.Lots.Should().Be(0.1m);
        pos.EntryPrice.Value.Should().Be(1.0850m);
    }

    [Fact]
    public async Task PartialThenFullFill_SecondFillTreatedAsDuplicate_PositionNeverCreated()
    {
        var (tracker, _, eventBus, riskMgr, _) = CreateTracker();

        var intent = new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
            new Price(1.0850m), new Price(1.0821m), "test", "standard", "", DateTime.UtcNow);
        var orderReq = new OrderRequest(intent, 0.2m, intent.Symbol, intent.Direction, OrderType.Market, intent.LimitPrice);
        var orderId = Guid.NewGuid();

        tracker.TrackOrder(orderId, orderReq, 28m);

        var partial = new ExecutionEvent(orderId, OrderState.Filled, new Price(1.0850m), 0.1m, null, DateTime.UtcNow);
        await tracker.OnExecutionAsync(partial, []);

        tracker.OpenPositions.Should().BeEmpty();

        var remainder = new ExecutionEvent(orderId, OrderState.Filled, new Price(1.0852m), 0.1m, null, DateTime.UtcNow.AddMinutes(1));
        await tracker.OnExecutionAsync(remainder, []);

        tracker.OpenPositions.Should().BeEmpty();
    }

    [Fact]
    public async Task DuplicateExecution_WhilePositionOpen_ClosesPosition()
    {
        var (tracker, _, eventBus, riskMgr, _) = CreateTracker();

        var intent = new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
            new Price(1.0850m), new Price(1.0821m), "test", "standard", "", DateTime.UtcNow);
        var orderReq = new OrderRequest(intent, 0.1m, intent.Symbol, intent.Direction, OrderType.Market, intent.LimitPrice);
        var orderId = Guid.NewGuid();

        tracker.TrackOrder(orderId, orderReq, 28m);

        var evt = new ExecutionEvent(orderId, OrderState.Filled, new Price(1.0850m), 0.1m, null, DateTime.UtcNow);
        await tracker.OnExecutionAsync(evt, []);

        tracker.OpenPositions.Should().HaveCount(1);

        var dup = new ExecutionEvent(orderId, OrderState.Filled, new Price(1.0860m), 0.1m, null, DateTime.UtcNow.AddMinutes(1));
        await tracker.OnExecutionAsync(dup, []);

        tracker.OpenPositions.Should().BeEmpty();
    }

    [Fact]
    public async Task CloseAtSL_RemovesPosition_EmitsTradeClosed()
    {
        var (tracker, _, eventBus, riskMgr, governor) = CreateTracker();

        var intent = new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
            new Price(1.0850m), new Price(1.0821m), "test", "standard", "", DateTime.UtcNow);
        var orderReq = new OrderRequest(intent, 0.1m, intent.Symbol, intent.Direction, OrderType.Market, intent.LimitPrice);
        var orderId = Guid.NewGuid();

        tracker.TrackOrder(orderId, orderReq, 28m);

        var openEvt = new ExecutionEvent(orderId, OrderState.Filled, new Price(1.0850m), 0.1m, null, DateTime.UtcNow);
        await tracker.OnExecutionAsync(openEvt, []);

        var closeEvt = new ExecutionEvent(orderId, OrderState.Filled, new Price(1.0821m), 0m, null, DateTime.UtcNow.AddMinutes(1));
        await tracker.OnExecutionAsync(closeEvt, []);

        tracker.OpenPositions.Should().BeEmpty();
    }

    [Fact]
    public async Task CloseAtTP_RemovesPosition()
    {
        var (tracker, _, eventBus, riskMgr, _) = CreateTracker();

        var intent = new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
            new Price(1.0850m), new Price(1.0821m), "test", "standard", "", DateTime.UtcNow);
        intent = intent with { TakeProfit = new Price(1.0880m) };
        var orderReq = new OrderRequest(intent, 0.1m, intent.Symbol, intent.Direction, OrderType.Market, intent.LimitPrice);
        var orderId = Guid.NewGuid();

        tracker.TrackOrder(orderId, orderReq, 28m);

        var openEvt = new ExecutionEvent(orderId, OrderState.Filled, new Price(1.0850m), 0.1m, null, DateTime.UtcNow);
        await tracker.OnExecutionAsync(openEvt, []);

        var closeEvt = new ExecutionEvent(orderId, OrderState.Filled, new Price(1.0880m), 0m, null, DateTime.UtcNow.AddMinutes(1));
        await tracker.OnExecutionAsync(closeEvt, []);

        tracker.OpenPositions.Should().BeEmpty();
    }

    [Fact]
    public async Task ForceClose_RemovesPosition()
    {
        var (tracker, _, eventBus, riskMgr, _) = CreateTracker();

        var intent = new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
            new Price(1.0850m), new Price(1.0821m), "test", "standard", "", DateTime.UtcNow);
        var orderReq = new OrderRequest(intent, 0.1m, intent.Symbol, intent.Direction, OrderType.Market, intent.LimitPrice);
        var orderId = Guid.NewGuid();

        tracker.TrackOrder(orderId, orderReq, 28m);

        var openEvt = new ExecutionEvent(orderId, OrderState.Filled, new Price(1.0850m), 0.1m, null, DateTime.UtcNow);
        await tracker.OnExecutionAsync(openEvt, []);

        var closeEvt = new ExecutionEvent(orderId, OrderState.Filled, new Price(1.0840m), 0m, null, DateTime.UtcNow.AddMinutes(1));
        await tracker.OnExecutionAsync(closeEvt, []);

        tracker.OpenPositions.Should().BeEmpty();
    }

    [Fact]
    public async Task OrderRejected_RemovesFromPending()
    {
        var (tracker, _, eventBus, riskMgr, _) = CreateTracker();

        var intent = new TradeIntent(Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
            new Price(1.0850m), new Price(1.0821m), "test", "standard", "", DateTime.UtcNow);
        var orderReq = new OrderRequest(intent, 0.1m, intent.Symbol, intent.Direction, OrderType.Market, intent.LimitPrice);
        var orderId = Guid.NewGuid();

        tracker.TrackOrder(orderId, orderReq, 28m);

        var reject = new ExecutionEvent(orderId, OrderState.Rejected, null, 0m, "InsufficientMargin", DateTime.UtcNow);
        await tracker.OnExecutionAsync(reject, []);

        tracker.OpenPositions.Should().BeEmpty();
    }
}

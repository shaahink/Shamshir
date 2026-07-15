using Microsoft.Extensions.Logging.Abstractions;

namespace TradingEngine.Tests.Unit.Phase31Tests;

[Trait("Category", "Infrastructure")]
public sealed class BacktestReplayCostsAndLimitsTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    private static BacktestReplayAdapter MakeAdapter(decimal commissionPerSide = 3.5m)
    {
        var repo = Substitute.For<IBarRepository>();
        repo.GetAsync(Arg.Any<Symbol>(), Arg.Any<Timeframe>(),
                      Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bar>>([]));

        var symbolInfo = new SymbolInfo(Eurusd, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m,
            "USD", commissionPerSide, 0m, 0m, "Wednesday");
        var registry = Substitute.For<ISymbolInfoRegistry>();
        registry.Get(Eurusd).Returns(symbolInfo);

        return new BacktestReplayAdapter(
            repo, Eurusd, Timeframe.H1, T0, T0.AddDays(1),
            10_000m, registry, (_, _) => 1.0m,
            NullLogger<BacktestReplayAdapter>.Instance);
    }

    private static Bar Bar(decimal open, decimal high, decimal low, decimal close, int hour = 0)
        => new(Eurusd, Timeframe.H1, T0.AddHours(hour), open, high, low, close, 1000);

    private static List<ExecutionEvent> Drain(BacktestReplayAdapter a)
    {
        var list = new List<ExecutionEvent>();
        while (a.ExecutionStream.TryRead(out var e)) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Full_cycle_close_is_net_of_commission()
    {
        var adapter = MakeAdapter(commissionPerSide: 3.5m);
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));

        var intent = new TradeIntent(Eurusd, TradeDirection.Long, OrderType.Market, null,
            new Price(1.0950m), new Price(1.1100m), "test", "standard", "", T0);
        var orderId = await adapter.SubmitOrderAsync(
            new OrderRequest(intent, 1.0m, Eurusd, TradeDirection.Long, OrderType.Market, null),
            CancellationToken.None);
        Drain(adapter); // discard the entry fill

        await adapter.ClosePositionAtAsync(orderId, new Price(1.1050m), CancellationToken.None);

        var close = Drain(adapter).Single();
        close.NewState.Should().Be(OrderState.Filled);
        close.Commission.Should().Be(-7m, "1 lot × $3.5/side × 2 = round-turn $7; costs are negative");
        close.GrossProfit.Should().NotBeNull();
        close.NetProfit.Should().Be(close.GrossProfit!.Value + (-7m), "net = gross + commission + swap (costs negative)");
        close.NetProfit.Should().BeLessThan(close.GrossProfit!.Value, "costs must reduce the net");
    }

    [Fact]
    public async Task Limit_order_rests_until_bar_reaches_it_then_fills_at_limit()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m, hour: 0));

        var limitPrice = new Price(1.0980m); // below market — a resting buy limit
        var intent = new TradeIntent(Eurusd, TradeDirection.Long, OrderType.Limit, limitPrice,
            new Price(1.0930m), new Price(1.1080m), "test", "standard", "", T0)
        {
            Entry = new OrderEntryOptions { Method = OrderEntryMethod.LimitOffset, LimitOrderExpiryBars = 3 }
        };
        var orderId = await adapter.SubmitOrderAsync(
            new OrderRequest(intent, 1.0m, Eurusd, TradeDirection.Long, OrderType.Limit, limitPrice),
            CancellationToken.None);

        Drain(adapter).Should().BeEmpty("a resting limit must NOT fill at submit");

        // Next bar's range does not reach the limit → still resting.
        adapter.OnBarObserved(Bar(1.1000m, 1.1010m, 1.0985m, 1.1000m, hour: 1));
        Drain(adapter).Should().BeEmpty("limit not reached yet");

        // A bar that trades down through the limit fills it. P4.3 (F43): the fill is the first O/H/L/C
        // tick to breach the limit, not the limit itself. A buy executes at the ASK, so the bid bar
        // O=1.0990 L=1.0975 becomes ask O=1.0991 L=1.0976 (spread 0.0001); the open has not breached the
        // 1.0980 limit, so the ask LOW is the fill — at-or-BETTER than the limit, exactly as the venue
        // fills it (measured: a sell limit at 1.15973 filled at 1.15975).
        adapter.OnBarObserved(Bar(1.0990m, 1.0995m, 1.0975m, 1.0985m, hour: 2));
        var fill = Drain(adapter).Single();
        fill.OrderId.Should().Be(orderId);
        fill.NewState.Should().Be(OrderState.Filled);
        fill.FillPrice!.Value.Value.Should().Be(1.0976m);
    }

    [Fact]
    public async Task Limit_order_expires_with_cancellation_carrying_ENTRY_EXPIRED()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m, hour: 0));

        var limitPrice = new Price(1.0800m); // far below — never reached
        var intent = new TradeIntent(Eurusd, TradeDirection.Long, OrderType.Limit, limitPrice,
            new Price(1.0750m), new Price(1.0900m), "test", "standard", "", T0)
        {
            Entry = new OrderEntryOptions { Method = OrderEntryMethod.LimitOffset, LimitOrderExpiryBars = 2 }
        };
        var orderId = await adapter.SubmitOrderAsync(
            new OrderRequest(intent, 1.0m, Eurusd, TradeDirection.Long, OrderType.Limit, limitPrice),
            CancellationToken.None);

        adapter.OnBarObserved(Bar(1.1000m, 1.1010m, 1.0990m, 1.1000m, hour: 1)); // burns 1 bar
        Drain(adapter).Should().BeEmpty();

        adapter.OnBarObserved(Bar(1.1000m, 1.1010m, 1.0990m, 1.1000m, hour: 2)); // burns 2nd → expires
        var cancel = Drain(adapter).Single();
        cancel.OrderId.Should().Be(orderId);
        cancel.NewState.Should().Be(OrderState.Cancelled);
        cancel.RejectionReason.Should().Be("ENTRY_EXPIRED");
    }
}

using Microsoft.Extensions.Logging.Abstractions;

namespace TradingEngine.Tests.Unit.Phase31Tests;

/// <summary>
/// P2.7: resting STOP entry orders — the mirror image of the limit-order tests in
/// <see cref="BacktestReplaySpreadConventionTests"/>. A buy stop fills when price rises UP THROUGH the
/// trigger (ask side, same long-entry convention as a market/limit buy); a sell stop fills when price
/// falls DOWN THROUGH it (raw bid, same short-entry convention). Table-driven, hand-computed literals
/// against a 2-pip (0.0002) spread. Mirrors <see cref="TapeReplayStopOrderTests"/>.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class BacktestReplayStopOrderTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private const decimal Spread = 0.0002m; // 2 pips

    private static BacktestReplayAdapter MakeAdapter()
    {
        var repo = Substitute.For<IBarRepository>();
        repo.GetAsync(Arg.Any<Symbol>(), Arg.Any<Timeframe>(),
                      Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Bar>>([]));

        var symbolInfo = new SymbolInfo(Eurusd, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, Spread);
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

    private static async Task<Guid> SubmitStop(BacktestReplayAdapter a, TradeDirection dir, Price stop, Price sl, Price? tp, int? expiryBars = null)
    {
        var entry = expiryBars is { } e ? new OrderEntryOptions { LimitOrderExpiryBars = e } : null;
        var intent = new TradeIntent(Eurusd, dir, OrderType.Stop, stop, sl, tp, "test", "standard", "", T0) { Entry = entry };
        return await a.SubmitOrderAsync(new OrderRequest(intent, 1.0m, Eurusd, dir, OrderType.Stop, stop), CancellationToken.None);
    }

    [Fact]
    public async Task BuyStop_ReachedWhenAskCrossesTrigger_NoGap_FillsAtStopPrice()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        var stop = new Price(1.1010m);
        var orderId = await SubmitStop(adapter, TradeDirection.Long, stop, new Price(1.0950m), new Price(1.1100m));
        Drain(adapter).Should().BeEmpty("resting stop must not fill at submit");

        // ask(high) = 1.1010 + 0.0002 = 1.1012 >= stop(1.1010) -> reached. ask(open) = 1.1005+0.0002=1.1007 < stop -> no gap.
        adapter.OnBarObserved(Bar(1.1005m, 1.1010m, 1.1000m, 1.1008m, hour: 1));

        var fill = Drain(adapter).Single();
        fill.OrderId.Should().Be(orderId);
        fill.FillPrice!.Value.Value.Should().Be(1.1010m, "buy stop fills at the trigger price when there's no gap-through");
    }

    [Fact]
    public async Task BuyStop_GapsThroughAtOpen_FillsAtOpen_NotTrigger()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        var stop = new Price(1.1010m);
        await SubmitStop(adapter, TradeDirection.Long, stop, new Price(1.0950m), new Price(1.1100m));
        Drain(adapter);

        // ask(open) = 1.1020+0.0002 = 1.1022 >= stop(1.1010) -> the bar already opened past the trigger.
        adapter.OnBarObserved(Bar(1.1020m, 1.1025m, 1.1015m, 1.1020m, hour: 1));

        var fill = Drain(adapter).Single();
        fill.FillPrice!.Value.Value.Should().Be(1.1022m, "a gap through the trigger fills at the (worse) ask-adjusted open, not the stop price");
    }

    [Fact]
    public async Task SellStop_ReachedWhenRawBidCrossesTrigger_NoGap_FillsAtStopPrice_NoSpreadAdjustment()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        var stop = new Price(1.0990m);
        var orderId = await SubmitStop(adapter, TradeDirection.Short, stop, new Price(1.1050m), new Price(1.0900m));
        Drain(adapter).Should().BeEmpty();

        // raw low 1.0988 <= stop(1.0990) -> reached. open 1.0995 > stop -> no gap.
        adapter.OnBarObserved(Bar(1.0995m, 1.1000m, 1.0988m, 1.0990m, hour: 1));

        var fill = Drain(adapter).Single();
        fill.OrderId.Should().Be(orderId);
        fill.FillPrice!.Value.Value.Should().Be(1.0990m, "sell stop fills at the raw trigger level — unadjusted");
    }

    [Fact]
    public async Task SellStop_GapsThroughAtOpen_FillsAtOpen_NotTrigger()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        var stop = new Price(1.0990m);
        await SubmitStop(adapter, TradeDirection.Short, stop, new Price(1.1050m), new Price(1.0900m));
        Drain(adapter);

        // raw open 1.0980 <= stop(1.0990) -> the bar already opened past the trigger.
        adapter.OnBarObserved(Bar(1.0980m, 1.0985m, 1.0975m, 1.0980m, hour: 1));

        var fill = Drain(adapter).Single();
        fill.FillPrice!.Value.Value.Should().Be(1.0980m, "a gap through the trigger fills at the (worse) open, not the stop price");
    }

    [Fact]
    public async Task Stop_ExpiresAfterConfiguredBars_EmitsEntryExpired()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        var stop = new Price(1.1500m); // far away — never reached by the fixture bars below
        var orderId = await SubmitStop(adapter, TradeDirection.Long, stop, new Price(1.0950m), new Price(1.1600m), expiryBars: 2);
        Drain(adapter).Should().BeEmpty();

        adapter.OnBarObserved(Bar(1.1000m, 1.1010m, 1.0990m, 1.1000m, hour: 1));
        Drain(adapter).Should().BeEmpty("1 bar elapsed of a 2-bar expiry — not expired yet");

        adapter.OnBarObserved(Bar(1.1000m, 1.1010m, 1.0990m, 1.1000m, hour: 2));
        var evt = Drain(adapter).Single();
        evt.OrderId.Should().Be(orderId);
        evt.RejectionReason.Should().Be("ENTRY_EXPIRED");
    }
}

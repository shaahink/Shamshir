using Microsoft.Extensions.Logging.Abstractions;

namespace TradingEngine.Tests.Unit.Phase31Tests;

/// <summary>
/// P0.2 (D3): one shared bid-bar convention — ask(t) = bid(t) + spread(t); longs buy@ask/sell@bid,
/// shorts sell@bid/buy@ask. Table-driven, hand-computed literals against a 2-pip (0.0002) spread, per
/// the plan's own prescription. Mirrors <see cref="TapeReplaySpreadConventionTests"/> — the two adapters
/// must never drift from each other again.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class BacktestReplaySpreadConventionTests
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

    private static async Task<Guid> Submit(BacktestReplayAdapter a, TradeDirection dir, Price sl, Price? tp, OrderType type = OrderType.Market, Price? limit = null)
    {
        var intent = new TradeIntent(Eurusd, dir, type, limit, sl, tp, "test", "standard", "", T0);
        return await a.SubmitOrderAsync(new OrderRequest(intent, 1.0m, Eurusd, dir, type, limit), CancellationToken.None);
    }

    [Fact]
    public async Task LongMarketEntry_FillsAtAsk_BidPlusFullSpread()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));

        await Submit(adapter, TradeDirection.Long, new Price(1.0950m), new Price(1.1100m));

        var fill = Drain(adapter).Single();
        fill.FillPrice!.Value.Value.Should().Be(1.1002m, "long entry buys at ask = bid(1.1000) + full spread(0.0002)");
    }

    [Fact]
    public async Task ShortMarketEntry_FillsAtBid_Unadjusted()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));

        await Submit(adapter, TradeDirection.Short, new Price(1.1050m), new Price(1.0900m));

        var fill = Drain(adapter).Single();
        fill.FillPrice!.Value.Value.Should().Be(1.1000m, "short entry sells at bid — raw close, no spread charged");
    }

    [Fact]
    public async Task LongLimitEntry_ReachedWhenAskCrossesLimit_FillsAtLimit()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        var limit = new Price(1.0980m);
        var orderId = await Submit(adapter, TradeDirection.Long, new Price(1.0930m), new Price(1.1080m), OrderType.Limit, limit);
        Drain(adapter).Should().BeEmpty("resting limit must not fill at submit");

        // raw low 1.0978; ask = 1.0978 + 0.0002 = 1.0980 == limit -> reached
        adapter.OnBarObserved(Bar(1.0985m, 1.0990m, 1.0978m, 1.0980m, hour: 1));

        var fill = Drain(adapter).Single();
        fill.OrderId.Should().Be(orderId);
        fill.FillPrice!.Value.Value.Should().Be(1.0980m, "buy limit fills at the limit price once ask(=low+spread) reaches it");
    }

    [Fact]
    public async Task ShortLimitEntry_ReachedWhenRawBidCrossesLimit_FillsAtLimit_NoSpreadAdjustment()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        var limit = new Price(1.1020m);
        var orderId = await Submit(adapter, TradeDirection.Short, new Price(1.1070m), new Price(1.0920m), OrderType.Limit, limit);
        Drain(adapter).Should().BeEmpty();

        // raw high 1.1020 == limit -> reached, no spread shift for a sell limit (bid-side test)
        adapter.OnBarObserved(Bar(1.1010m, 1.1020m, 1.1005m, 1.1015m, hour: 1));

        var fill = Drain(adapter).Single();
        fill.OrderId.Should().Be(orderId);
        fill.FillPrice!.Value.Value.Should().Be(1.1020m, "sell limit fills once raw bid(high) reaches it — unadjusted");
    }

    [Fact]
    public async Task LongStopLoss_FillsAtRawStopLevel_NoSpreadAdjustment()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        await Submit(adapter, TradeDirection.Long, new Price(1.0950m), new Price(1.1100m));
        Drain(adapter);

        // raw low touches SL exactly; open does not gap through it
        adapter.OnBarObserved(Bar(1.0970m, 1.0975m, 1.0950m, 1.0960m, hour: 1));

        var close = Drain(adapter).Single();
        close.CloseReason.Should().Be("SL");
        close.FillPrice!.Value.Value.Should().Be(1.0950m, "long SL sells at bid — the raw stop level, unadjusted");
    }

    [Fact]
    public async Task LongTakeProfit_FillsAtRawTargetLevel_NoSpreadAdjustment()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        await Submit(adapter, TradeDirection.Long, new Price(1.0950m), new Price(1.1100m));
        Drain(adapter);

        adapter.OnBarObserved(Bar(1.1080m, 1.1100m, 1.1075m, 1.1090m, hour: 1));

        var close = Drain(adapter).Single();
        close.CloseReason.Should().Be("TP");
        close.FillPrice!.Value.Value.Should().Be(1.1100m, "long TP sells at bid — the raw target level, unadjusted");
    }

    [Fact]
    public async Task ShortStopLoss_FillsAtStopLevelPlusFullSpread()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        await Submit(adapter, TradeDirection.Short, new Price(1.1050m), new Price(1.0900m));
        Drain(adapter);

        // ask(=high+spread) must reach the SL: raw high 1.1048 + 0.0002 = 1.1050
        adapter.OnBarObserved(Bar(1.1030m, 1.1048m, 1.1020m, 1.1040m, hour: 1));

        var close = Drain(adapter).Single();
        close.CloseReason.Should().Be("SL");
        close.FillPrice!.Value.Value.Should().Be(1.1052m, "short SL buys at ask = stop level(1.1050) + full spread(0.0002)");
    }

    [Fact]
    public async Task ShortTakeProfit_FillsAtTargetLevelPlusFullSpread()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        await Submit(adapter, TradeDirection.Short, new Price(1.1050m), new Price(1.0900m));
        Drain(adapter);

        // ask(=low+spread) must reach the TP: raw low 1.0898 + 0.0002 = 1.0900
        adapter.OnBarObserved(Bar(1.0970m, 1.0975m, 1.0898m, 1.0960m, hour: 1));

        var close = Drain(adapter).Single();
        close.CloseReason.Should().Be("TP");
        close.FillPrice!.Value.Value.Should().Be(1.0902m, "short TP buys at ask = target level(1.0900) + full spread(0.0002)");
    }
}

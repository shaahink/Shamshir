using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Tests.Unit.Phase31Tests;

/// <summary>
/// P0.2 (D3): same 8 fill-path cases as <see cref="BacktestReplaySpreadConventionTests"/>, against
/// <see cref="TapeReplayAdapter"/> — the two adapters must never drift from each other again. Uses
/// decisionTf == exitTf so the adapter runs single-resolution exits (no ConnectAsync/feed needed; the
/// adapter is driven directly via SubmitOrderAsync/OnBarObserved, same as the Backtest adapter's tests).
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class TapeReplaySpreadConventionTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private const decimal Spread = 0.0002m; // 2 pips

    private static TapeReplayAdapter MakeAdapter()
    {
        var store = Substitute.For<IMarketDataStore>();

        var symbolInfo = new SymbolInfo(Eurusd, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, Spread);
        var registry = Substitute.For<ISymbolInfoRegistry>();
        registry.Get(Eurusd).Returns(symbolInfo);

        return new TapeReplayAdapter(
            store, Eurusd, Timeframe.H1, Timeframe.H1, T0, T0.AddDays(1),
            10_000m, registry, (_, _) => 1.0m,
            NullLogger<TapeReplayAdapter>.Instance);
    }

    private static Bar Bar(decimal open, decimal high, decimal low, decimal close, int hour = 0)
        => new(Eurusd, Timeframe.H1, T0.AddHours(hour), open, high, low, close, 1000);

    private static List<ExecutionEvent> Drain(TapeReplayAdapter a)
    {
        var list = new List<ExecutionEvent>();
        while (a.ExecutionStream.TryRead(out var e)) list.Add(e);
        return list;
    }

    private static async Task<Guid> Submit(TapeReplayAdapter a, TradeDirection dir, Price sl, Price? tp, OrderType type = OrderType.Market, Price? limit = null)
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
        fill.FillPrice!.Value.Value.Should().Be(1.1002m);
    }

    [Fact]
    public async Task ShortMarketEntry_FillsAtBid_Unadjusted()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));

        await Submit(adapter, TradeDirection.Short, new Price(1.1050m), new Price(1.0900m));

        var fill = Drain(adapter).Single();
        fill.FillPrice!.Value.Value.Should().Be(1.1000m);
    }

    [Fact]
    public async Task LongLimitEntry_ReachedWhenAskCrossesLimit_FillsAtLimit()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        var limit = new Price(1.0980m);
        var orderId = await Submit(adapter, TradeDirection.Long, new Price(1.0930m), new Price(1.1080m), OrderType.Limit, limit);
        Drain(adapter).Should().BeEmpty();

        adapter.OnBarObserved(Bar(1.0985m, 1.0990m, 1.0978m, 1.0980m, hour: 1));

        var fill = Drain(adapter).Single();
        fill.OrderId.Should().Be(orderId);
        fill.FillPrice!.Value.Value.Should().Be(1.0980m);
    }

    [Fact]
    public async Task ShortLimitEntry_ReachedWhenRawBidCrossesLimit_FillsAtLimit_NoSpreadAdjustment()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        var limit = new Price(1.1020m);
        var orderId = await Submit(adapter, TradeDirection.Short, new Price(1.1070m), new Price(1.0920m), OrderType.Limit, limit);
        Drain(adapter).Should().BeEmpty();

        adapter.OnBarObserved(Bar(1.1010m, 1.1020m, 1.1005m, 1.1015m, hour: 1));

        var fill = Drain(adapter).Single();
        fill.OrderId.Should().Be(orderId);
        fill.FillPrice!.Value.Value.Should().Be(1.1020m);
    }

    [Fact]
    public async Task LongStopLoss_FillsAtRawStopLevel_NoSpreadAdjustment()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        await Submit(adapter, TradeDirection.Long, new Price(1.0950m), new Price(1.1100m));
        Drain(adapter);

        adapter.OnBarObserved(Bar(1.0970m, 1.0975m, 1.0950m, 1.0960m, hour: 1));

        var close = Drain(adapter).Single();
        close.CloseReason.Should().Be("SL");
        close.FillPrice!.Value.Value.Should().Be(1.0950m);
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
        close.FillPrice!.Value.Value.Should().Be(1.1100m);
    }

    [Fact]
    public async Task ShortStopLoss_FillsAtStopLevelPlusFullSpread()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        await Submit(adapter, TradeDirection.Short, new Price(1.1050m), new Price(1.0900m));
        Drain(adapter);

        adapter.OnBarObserved(Bar(1.1030m, 1.1048m, 1.1020m, 1.1040m, hour: 1));

        var close = Drain(adapter).Single();
        close.CloseReason.Should().Be("SL");
        close.FillPrice!.Value.Value.Should().Be(1.1052m);
    }

    [Fact]
    public async Task ShortTakeProfit_FillsAtTargetLevelPlusFullSpread()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));
        await Submit(adapter, TradeDirection.Short, new Price(1.1050m), new Price(1.0900m));
        Drain(adapter);

        adapter.OnBarObserved(Bar(1.0970m, 1.0975m, 1.0898m, 1.0960m, hour: 1));

        var close = Drain(adapter).Single();
        close.CloseReason.Should().Be("TP");
        close.FillPrice!.Value.Value.Should().Be(1.0902m);
    }
}

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Tests.Unit.Phase31Tests;

/// <summary>
/// P3.1: the opt-in excursion recorder in <see cref="TapeReplayAdapter"/> (single-resolution mode — same
/// harness shape as <see cref="TapeReplayStopOrderTests"/>). Proves the accumulated path attaches to the
/// CLOSE execution event with hand-computed pip values, for both a long (SL-hit close) and a short
/// (force-close) position, and that RecordExcursions=false (the default) produces no path at all.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class TapeReplayExcursionRecorderTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private const decimal Spread = 0.0002m; // 2 pips

    private sealed record PointDto(int t, double hi, double lo);

    private static TapeReplayAdapter MakeAdapter(bool recordExcursions)
    {
        var store = Substitute.For<IMarketDataStore>();

        var symbolInfo = new SymbolInfo(Eurusd, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, Spread);
        var registry = Substitute.For<ISymbolInfoRegistry>();
        registry.Get(Eurusd).Returns(symbolInfo);

        return new TapeReplayAdapter(
            store, Eurusd, Timeframe.H1, Timeframe.H1, T0, T0.AddDays(1),
            10_000m, registry, (_, _) => 1.0m,
            NullLogger<TapeReplayAdapter>.Instance,
            honestFills: true, recordExcursions: recordExcursions);
    }

    private static Bar Bar(decimal open, decimal high, decimal low, decimal close, int hour = 0)
        => new(Eurusd, Timeframe.H1, T0.AddHours(hour), open, high, low, close, 1000);

    private static List<ExecutionEvent> Drain(TapeReplayAdapter a)
    {
        var list = new List<ExecutionEvent>();
        while (a.ExecutionStream.TryRead(out var e)) list.Add(e);
        return list;
    }

    private static async Task<Guid> SubmitMarket(TapeReplayAdapter a, TradeDirection dir, Price sl, Price? tp)
    {
        var intent = new TradeIntent(Eurusd, dir, OrderType.Market, null, sl, tp, "test", "standard", "", T0);
        return await a.SubmitOrderAsync(new OrderRequest(intent, 1.0m, Eurusd, dir, OrderType.Market, null), CancellationToken.None);
    }

    [Fact]
    public async Task Long_AccumulatesPathAcrossFineBars_AttachesOnSlClose()
    {
        var adapter = MakeAdapter(recordExcursions: true);
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m)); // _lastClose=1.1000

        var orderId = await SubmitMarket(adapter, TradeDirection.Long, new Price(1.0950m), new Price(1.1200m));
        Drain(adapter); // entry fill at ask(1.1000)=1.1002

        // bar2: hi=(1.1010-1.1002)/0.0001=8.0, lo=(1.0998-1.1002)/0.0001=-4.0, t=0 (entry stamped at bar2's open)
        adapter.OnBarObserved(Bar(1.1002m, 1.1010m, 1.0998m, 1.1005m, hour: 1));
        Drain(adapter).Should().BeEmpty("no SL/TP hit yet");

        // bar3: hi=(1.0975-1.1002)/0.0001=-27.0, lo=(1.0950-1.1002)/0.0001=-52.0, t=60min; raw low touches SL exactly.
        adapter.OnBarObserved(Bar(1.0970m, 1.0975m, 1.0950m, 1.0960m, hour: 2));

        var close = Drain(adapter).Single();
        close.OrderId.Should().Be(orderId);
        close.CloseReason.Should().Be("SL");
        close.ExcursionPathJson.Should().NotBeNull();

        var path = JsonSerializer.Deserialize<List<PointDto>>(close.ExcursionPathJson!)!;
        path.Should().HaveCount(2);
        path[0].t.Should().Be(0);
        path[0].hi.Should().BeApproximately(8.0, 0.01);
        path[0].lo.Should().BeApproximately(-4.0, 0.01);
        path[1].t.Should().Be(60);
        path[1].hi.Should().BeApproximately(-27.0, 0.01);
        path[1].lo.Should().BeApproximately(-52.0, 0.01);
    }

    [Fact]
    public async Task Short_AccumulatesPath_AttachesOnForceClose()
    {
        var adapter = MakeAdapter(recordExcursions: true);
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m)); // _lastClose=1.1000

        var orderId = await SubmitMarket(adapter, TradeDirection.Short, new Price(1.1050m), new Price(1.0900m));
        Drain(adapter); // short entry fills at raw bid 1.1000, no spread adjustment

        // bar2: hi=(1.1002-1.1000)/0.0001=2.0, lo=(1.0985-1.1000)/0.0001=-15.0, t=0; no SL/TP hit.
        adapter.OnBarObserved(Bar(1.0995m, 1.1002m, 1.0985m, 1.0990m, hour: 1));
        Drain(adapter).Should().BeEmpty();

        await adapter.ClosePositionAsync(orderId, CancellationToken.None);
        var close = Drain(adapter).Single();
        close.OrderId.Should().Be(orderId);
        close.ExcursionPathJson.Should().NotBeNull();

        var path = JsonSerializer.Deserialize<List<PointDto>>(close.ExcursionPathJson!)!;
        path.Should().ContainSingle();
        path[0].t.Should().Be(0);
        path[0].hi.Should().BeApproximately(2.0, 0.01);
        path[0].lo.Should().BeApproximately(-15.0, 0.01);
    }

    [Fact]
    public async Task RecordExcursionsDisabled_ProducesNoPath()
    {
        var adapter = MakeAdapter(recordExcursions: false); // the default
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));

        var orderId = await SubmitMarket(adapter, TradeDirection.Long, new Price(1.0950m), new Price(1.1200m));
        Drain(adapter);

        adapter.OnBarObserved(Bar(1.1002m, 1.1010m, 1.0998m, 1.1005m, hour: 1));
        adapter.OnBarObserved(Bar(1.0970m, 1.0975m, 1.0950m, 1.0960m, hour: 2)); // same SL hit as the long test above

        var close = Drain(adapter).Single();
        close.OrderId.Should().Be(orderId);
        close.CloseReason.Should().Be("SL");
        close.ExcursionPathJson.Should().BeNull("RecordExcursions defaults off — zero rows, zero overhead");
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Services;

namespace TradingEngine.Tests.Unit.Services;

/// <summary>
/// Covers every <see cref="OrderEntryMethod"/> branch of <see cref="EntryPlanner.Plan"/>. P2.7 added
/// <see cref="OrderEntryMethod.StopConfirm"/> (a resting stop trigger at the signal bar's High/Low + a
/// spread-multiple buffer) — Market/MarketWithSlippage/LimitOffset are the pre-existing regression net.
/// </summary>
public sealed class EntryPlannerTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private const decimal PipSize = 0.0001m;
    private const decimal Spread = 0.0002m; // 2 pips

    private static EntryPlanner MakePlanner()
    {
        var symbolInfo = new SymbolInfo(Eurusd, SymbolCategory.Forex, "EUR", "USD",
            PipSize, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, Spread);
        var registry = Substitute.For<ISymbolInfoRegistry>();
        registry.Get(Eurusd).Returns(symbolInfo);
        return new EntryPlanner(registry, NullLogger<EntryPlanner>.Instance);
    }

    private static TradeIntent MakeIntent(TradeDirection dir, decimal sl, decimal? tp) => new(
        Eurusd, dir, OrderType.Market, null, new Price(sl), tp is { } t ? new Price(t) : null,
        "test-strategy", "standard", "signal reason", DateTime.UtcNow);

    private static Bar MakeBar(decimal open, decimal high, decimal low, decimal close) =>
        new(Eurusd, Timeframe.H1, DateTime.UtcNow, open, high, low, close, 1000);

    [Fact]
    public void Market_ReturnsIntentUnchanged()
    {
        var planner = MakePlanner();
        var intent = MakeIntent(TradeDirection.Long, 1.0990m, 1.1140m);

        var planned = planner.Plan(intent, new OrderEntryOptions { Method = OrderEntryMethod.Market }, 1.1040m);

        planned.Should().BeSameAs(intent);
    }

    [Fact]
    public void MarketWithSlippage_KeepsMarketOrderType_ButAttachesEntryOptions()
    {
        var planner = MakePlanner();
        var intent = MakeIntent(TradeDirection.Long, 1.0990m, 1.1140m);
        var entry = new OrderEntryOptions { Method = OrderEntryMethod.MarketWithSlippage, MaxSlippagePips = 3.0 };

        var planned = planner.Plan(intent, entry, 1.1040m);

        planned.OrderType.Should().Be(OrderType.Market);
        planned.LimitPrice.Should().BeNull();
        planned.Entry.Should().Be(entry);
    }

    [Fact]
    public void LimitOffset_Long_PlacesLimitBelowSignal_AndShiftsSlTpByTheSameDistance()
    {
        var planner = MakePlanner();
        var intent = MakeIntent(TradeDirection.Long, 1.0990m, 1.1140m); // signal 1.1040: SL -50p, TP +100p
        var entry = new OrderEntryOptions { Method = OrderEntryMethod.LimitOffset, LimitOffsetPips = 5.0 };

        var planned = planner.Plan(intent, entry, 1.1040m);

        planned.OrderType.Should().Be(OrderType.Limit);
        planned.LimitPrice!.Value.Value.Should().Be(1.1035m, "5-pip offset below the signal price");
        planned.StopLoss.Value.Should().Be(1.0985m, "SL shifts by the same 50-pip distance from the new entry");
        planned.TakeProfit!.Value.Value.Should().Be(1.1135m, "TP shifts by the same 100-pip distance from the new entry");
    }

    [Fact]
    public void LimitOffset_Short_PlacesLimitAboveSignal_AndShiftsSlTpByTheSameDistance()
    {
        var planner = MakePlanner();
        var intent = MakeIntent(TradeDirection.Short, 1.1090m, 1.0840m); // signal 1.1040: SL +50p, TP -200p
        var entry = new OrderEntryOptions { Method = OrderEntryMethod.LimitOffset, LimitOffsetPips = 5.0 };

        var planned = planner.Plan(intent, entry, 1.1040m);

        planned.OrderType.Should().Be(OrderType.Limit);
        planned.LimitPrice!.Value.Value.Should().Be(1.1045m, "5-pip offset above the signal price");
        planned.StopLoss.Value.Should().Be(1.1095m);
        planned.TakeProfit!.Value.Value.Should().Be(1.0845m);
    }

    [Fact]
    public void StopConfirm_Long_TriggersAtBarHighPlusBuffer_AndShiftsSlTpByTheSameDistance()
    {
        var planner = MakePlanner();
        var intent = MakeIntent(TradeDirection.Long, 1.0990m, 1.1140m); // signal 1.1040: SL -50p, TP +100p
        var entry = new OrderEntryOptions { Method = OrderEntryMethod.StopConfirm, StopConfirmBufferSpreadMultiple = 1.0 };
        var bar = MakeBar(1.1030m, 1.1050m, 1.1020m, 1.1040m);

        var planned = planner.Plan(intent, entry, 1.1040m, bar);

        planned.OrderType.Should().Be(OrderType.Stop);
        planned.LimitPrice!.Value.Value.Should().Be(1.1052m, "bar.High(1.1050) + 1x spread(0.0002) buffer");
        planned.StopLoss.Value.Should().Be(1.1002m, "SL shifts by the original 50-pip distance from the new trigger");
        planned.TakeProfit!.Value.Value.Should().Be(1.1152m, "TP shifts by the original 100-pip distance from the new trigger");
    }

    [Fact]
    public void StopConfirm_Short_TriggersAtBarLowMinusBuffer_AndShiftsSlTpByTheSameDistance()
    {
        var planner = MakePlanner();
        var intent = MakeIntent(TradeDirection.Short, 1.1010m, 1.0860m); // signal 1.0960: SL +50p, TP -100p
        var entry = new OrderEntryOptions { Method = OrderEntryMethod.StopConfirm, StopConfirmBufferSpreadMultiple = 1.0 };
        var bar = MakeBar(1.0970m, 1.0980m, 1.0950m, 1.0960m);

        var planned = planner.Plan(intent, entry, 1.0960m, bar);

        planned.OrderType.Should().Be(OrderType.Stop);
        planned.LimitPrice!.Value.Value.Should().Be(1.0948m, "bar.Low(1.0950) - 1x spread(0.0002) buffer");
        planned.StopLoss.Value.Should().Be(1.0998m);
        planned.TakeProfit!.Value.Value.Should().Be(1.0848m);
    }

    [Fact]
    public void StopConfirm_ScalesBufferWithConfiguredMultiple()
    {
        var planner = MakePlanner();
        var intent = MakeIntent(TradeDirection.Long, 1.0990m, null);
        var entry = new OrderEntryOptions { Method = OrderEntryMethod.StopConfirm, StopConfirmBufferSpreadMultiple = 2.5 };
        var bar = MakeBar(1.1030m, 1.1050m, 1.1020m, 1.1040m);

        var planned = planner.Plan(intent, entry, 1.1040m, bar);

        planned.LimitPrice!.Value.Value.Should().Be(1.1055m, "bar.High(1.1050) + 2.5x spread(0.0002) = +0.0005");
    }

    [Fact]
    public void StopConfirm_NoBarSupplied_FallsBackToSignalPriceAsBarExtreme()
    {
        var planner = MakePlanner();
        var intent = MakeIntent(TradeDirection.Long, 1.0990m, null);
        var entry = new OrderEntryOptions { Method = OrderEntryMethod.StopConfirm, StopConfirmBufferSpreadMultiple = 1.0 };

        var planned = planner.Plan(intent, entry, 1.1040m, bar: null);

        planned.LimitPrice!.Value.Value.Should().Be(1.1042m, "defensive fallback: signalPrice(1.1040) + buffer(0.0002)");
    }
}

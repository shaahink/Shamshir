using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Tests.Unit.Phase31Tests;

/// <summary>
/// F87 P3: every closed trade records what spread actually cost it (SpreadCost on the close
/// ExecutionEvent → TradeResults.SpreadCostAmount), so signal-vs-toll decomposition becomes a
/// column read instead of offline archaeology. Conventions under test (this venue's fill model,
/// P0.2/D3): a LONG crosses the spread at ENTRY (buys at ask), a SHORT crosses it at EXIT (buys
/// back at ask) — exactly one crossing per round trip, priced at the spread in force AT THAT fill.
/// SpreadCost is negative (R2) and SignalPnL ≡ Gross − SpreadCost equals the bid-to-bid PnL
/// recomputed from the same bars.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class TapeReplaySpreadCostTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private const decimal RegistrySpread = 0.0002m;
    private const decimal PipValue = 10m; // EURUSD, USD account: 0.0001 × 100,000

    private static TapeReplayAdapter MakeAdapter()
    {
        var store = Substitute.For<IMarketDataStore>();
        var symbolInfo = new SymbolInfo(Eurusd, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, RegistrySpread);
        var registry = Substitute.For<ISymbolInfoRegistry>();
        registry.Get(Eurusd).Returns(symbolInfo);

        return new TapeReplayAdapter(
            store, Eurusd, Timeframe.H1, Timeframe.H1, T0, T0.AddDays(1),
            10_000m, registry, (_, _) => 1.0m,
            NullLogger<TapeReplayAdapter>.Instance,
            spreadPipsOverride: null);
    }

    private static Bar Bar(decimal open, decimal high, decimal low, decimal close, int hour = 0, decimal? spread = null)
        => new(Eurusd, Timeframe.H1, T0.AddHours(hour), open, high, low, close, 1000, spread);

    private static List<ExecutionEvent> Drain(TapeReplayAdapter a)
    {
        var list = new List<ExecutionEvent>();
        while (a.ExecutionStream.TryRead(out var e)) list.Add(e);
        return list;
    }

    private static async Task<Guid> Submit(TapeReplayAdapter a, TradeDirection direction, decimal sl, decimal tp, decimal lots = 1.0m)
    {
        var intent = new TradeIntent(Eurusd, direction, OrderType.Market, null,
            new Price(sl), new Price(tp), "test", "standard", "", T0);
        return await a.SubmitOrderAsync(
            new OrderRequest(intent, lots, Eurusd, direction, OrderType.Market, null), CancellationToken.None);
    }

    [Fact]
    public async Task Long_PaysEntrySpread_SignalIdentityHolds()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m, hour: 0, spread: 0.00015m));
        await Submit(adapter, TradeDirection.Long, sl: 1.0950m, tp: 1.1100m);

        // TP bar carries a DIFFERENT spread — a long's toll must stay the entry-time 1.5 pips.
        adapter.OnBarObserved(Bar(1.1000m, 1.1120m, 1.0990m, 1.1110m, hour: 1, spread: 0.00030m));

        var close = Drain(adapter).Single(e => e.NetProfit is not null);
        close.SpreadCost.Should().Be(-15m,
            "long entry crossed 1.5 pips at entry: -(0.00015/0.0001) × $10/pip × 1 lot = -$15 (negative, R2)");

        // Gross from fills: entry 1.10015 (ask) → exit 1.1120 (first breaching tick of TP on the bid bar).
        close.GrossProfit.Should().Be(1185m);
        // SignalPnL ≡ Gross − SpreadCost = bid-to-bid (1.1000 → 1.1120 = 120 pips = $1,200).
        (close.GrossProfit!.Value - close.SpreadCost!.Value).Should().Be(1200m);
        // R2 invariant untouched: Net = Gross + Commission + Swap.
        close.NetProfit.Should().Be(close.GrossProfit + close.Commission + close.Swap);
    }

    [Fact]
    public async Task Short_PaysExitSpread_AtTheExitBarsRecordedSpread()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m, hour: 0, spread: 0.00015m));
        await Submit(adapter, TradeDirection.Short, sl: 1.1050m, tp: 1.0900m);

        // Exit bar's spread (3.0 pips) is what the short's buy-back crosses — NOT the entry bar's 1.5.
        adapter.OnBarObserved(Bar(1.1000m, 1.1010m, 1.0850m, 1.0870m, hour: 1, spread: 0.00030m));

        var close = Drain(adapter).Single(e => e.NetProfit is not null);
        close.SpreadCost.Should().Be(-30m,
            "short exit crossed the EXIT bar's 3.0 pips: -(0.0003/0.0001) × $10/pip × 1 lot = -$30");

        // Entry 1.1000 (raw bid) → exit 1.0853 (TP breached on the ask-shifted bar's low 1.0850+0.0003).
        close.GrossProfit.Should().Be(1470m);
        // SignalPnL = bid-to-bid: 1.1000 → 1.0850 = 150 pips = $1,500.
        (close.GrossProfit!.Value - close.SpreadCost!.Value).Should().Be(1500m);
        close.NetProfit.Should().Be(close.GrossProfit + close.Commission + close.Swap);
    }

    // F87 P4: floating PnL must read the SAME spread number as fills. Before P4 it read the
    // registry's TypicalSpread directly (one of the three divergent spread sources), so open-trade
    // equity — and therefore intrabar drawdown watermarks — was priced off a different spread than
    // the fills of the very same run.
    [Fact]
    public async Task FloatingPnL_UsesPerBarRecordedSpread_NotRegistryTypicalSpread()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m, hour: 0, spread: 0.00030m));
        await Submit(adapter, TradeDirection.Short, sl: 1.1050m, tp: 1.0900m);

        var updates = new List<AccountUpdate>();
        while (adapter.AccountStream.TryRead(out var u)) updates.Add(u);

        // Short's exit side is the ask (full-spread convention, R3): bid 1.1000 → ask 1.1003,
        // floating = −3.0 pips × $10 = −$30 off the RECORDED spread (registry would say −$20).
        updates[^1].FloatingPnL.Should().Be(-30m);
        updates[^1].Equity.Should().Be(9_970m);
    }

    [Fact]
    public async Task PartialClose_ProratesSpreadCost_LikeEntryCommission()
    {
        var adapter = MakeAdapter();
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m, hour: 0, spread: 0.00020m));
        var id = await Submit(adapter, TradeDirection.Long, sl: 1.0950m, tp: 1.1100m);
        Drain(adapter); // discard the entry fill

        await adapter.ClosePartialPositionAsync(id, 0.4m, CancellationToken.None);
        await adapter.ClosePositionAsync(id, CancellationToken.None);

        var closes = Drain(adapter).Where(e => e.NetProfit is not null).ToList();
        closes.Should().HaveCount(2);
        closes[0].SpreadCost.Should().Be(-8m, "the 0.4-lot leg carries 0.4 of the 2-pip entry toll: -2 × $10 × 0.4");
        closes[1].SpreadCost.Should().Be(-12m, "the remaining 0.6-lot leg carries the rest: -2 × $10 × 0.6");
        (closes[0].SpreadCost!.Value + closes[1].SpreadCost!.Value).Should().Be(-20m,
            "legs must sum to the full entry toll (2 pips × $10 × 1 lot) — same proration as EntryCommission");
    }
}

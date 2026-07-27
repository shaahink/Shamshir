using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Infrastructure.Adapters;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Tests.Unit.Phase31Tests;

/// <summary>
/// P3 (F32): before this fix, tape always fell back to the static symbols.json TypicalSpread
/// regardless of a run's configured spreadPips, while cTrader always honoured --spread (fed from the
/// same config value) — so a compare-both run asking for "one shared spread number" (PLAN.md P3(b))
/// silently got two different ones. spreadPipsOverride makes tape honour the SAME run-level number.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class TapeReplaySpreadOverrideTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private const decimal RegistrySpread = 0.0002m; // 2 pips — the static symbols.json-style value

    private static TapeReplayAdapter MakeAdapter(decimal? spreadPipsOverride)
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
            spreadPipsOverride: spreadPipsOverride);
    }

    private static Bar Bar(decimal open, decimal high, decimal low, decimal close, int hour = 0, decimal? spread = null)
        => new(Eurusd, Timeframe.H1, T0.AddHours(hour), open, high, low, close, 1000, spread);

    private static List<ExecutionEvent> Drain(TapeReplayAdapter a)
    {
        var list = new List<ExecutionEvent>();
        while (a.ExecutionStream.TryRead(out var e)) list.Add(e);
        return list;
    }

    private static async Task<Guid> SubmitLongMarket(TapeReplayAdapter a)
    {
        var intent = new TradeIntent(Eurusd, TradeDirection.Long, OrderType.Market, null,
            new Price(1.0950m), new Price(1.1100m), "test", "standard", "", T0);
        return await a.SubmitOrderAsync(
            new OrderRequest(intent, 1.0m, Eurusd, TradeDirection.Long, OrderType.Market, null), CancellationToken.None);
    }

    [Fact]
    public async Task NoOverride_UsesRegistryTypicalSpread()
    {
        var adapter = MakeAdapter(spreadPipsOverride: null);
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));

        await SubmitLongMarket(adapter);

        var fill = Drain(adapter).Single();
        fill.FillPrice!.Value.Value.Should().Be(1.1002m, "unadjusted: bid(1.1000) + registry spread(0.0002)");
    }

    [Fact]
    public async Task WithOverride_UsesRunSpreadPips_NotRegistryValue()
    {
        // 1 pip override on a 0.0001 pip-size symbol = 0.0001 price units — half the registry's 0.0002.
        var adapter = MakeAdapter(spreadPipsOverride: 1m);
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));

        await SubmitLongMarket(adapter);

        var fill = Drain(adapter).Single();
        fill.FillPrice!.Value.Value.Should().Be(1.1001m,
            "the run's spreadPips override (1 pip = 0.0001) must win over the registry's TypicalSpread (0.0002) — " +
            "this is what makes tape and cTrader use the SAME spread number for parity (PLAN.md P3(b))");
    }

    // F87: the decisive null-path test. Before F87 every run carried a non-null override (the DTO
    // default was 1), so this branch — recorded per-bar spread pricing the fill — was unreachable.
    // Null override + a bar carrying Spread must fill at THAT bar's spread, and a later bar with a
    // different recorded spread must produce a different fill: the flat-spread world is provably gone.
    [Fact]
    public async Task NoOverride_BarWithRecordedSpread_FillsAtThatBarsSpread()
    {
        var adapter = MakeAdapter(spreadPipsOverride: null);
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m, spread: 0.00015m));

        await SubmitLongMarket(adapter);

        var fill = Drain(adapter).Single();
        fill.FillPrice!.Value.Value.Should().Be(1.10015m,
            "null override must fall through to the bar's RECORDED spread (0.00015), not the registry's TypicalSpread (0.0002)");
    }

    [Fact]
    public async Task NoOverride_RecordedSpreadVariesPerBar_FillsVaryWithIt()
    {
        var adapter = MakeAdapter(spreadPipsOverride: null);

        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m, hour: 0, spread: 0.00015m));
        await SubmitLongMarket(adapter);

        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m, hour: 1, spread: 0.00030m));
        await SubmitLongMarket(adapter);

        var fills = Drain(adapter);
        fills.Should().HaveCount(2);
        fills[0].FillPrice!.Value.Value.Should().Be(1.10015m, "first bar's recorded spread is 1.5 pips");
        fills[1].FillPrice!.Value.Value.Should().Be(1.10030m,
            "the SECOND bar's different recorded spread (3.0 pips) must price the second fill — a flat spread cannot do this");
    }

    [Fact]
    public async Task WithOverride_ZeroIsAValidExplicitValue_NotTreatedAsUnset()
    {
        // 0 is a legitimate "no spread" test configuration — must not fall through to the registry.
        var adapter = MakeAdapter(spreadPipsOverride: 0m);
        adapter.OnBarObserved(Bar(1.1000m, 1.1005m, 1.0995m, 1.1000m));

        await SubmitLongMarket(adapter);

        var fill = Drain(adapter).Single();
        fill.FillPrice!.Value.Value.Should().Be(1.1000m, "an explicit 0-pip override means zero spread, not 'fall back to registry'");
    }
}

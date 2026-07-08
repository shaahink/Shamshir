using System;
using System.Collections.Generic;
using NSubstitute;
using TradingEngine.Services.Strategy.Filters;

namespace TradingEngine.Tests.Unit.Services;

public class SpreadVolNoTradeFilterTests
{
    private static readonly SymbolInfo EurUsd = new(
        new Symbol("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100000m, 0.01m, 100m, 0.01m, 0.0333m, 1.0m);

    private SpreadVolNoTradeFilter MakeFilter(decimal maxSpread, decimal maxAtr, string atrKey = "ATR14")
    {
        var reg = Substitute.For<ISymbolInfoRegistry>();
        reg.Get(Arg.Any<Symbol>()).Returns(EurUsd);
        return new SpreadVolNoTradeFilter(maxSpread, maxAtr, atrKey, reg);
    }

    private static MarketContext MakeCtx(decimal askBidSpread, double? atrPips = null)
    {
        var symbol = new Symbol("EURUSD");
        var ts = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var bar = new Bar(symbol, Timeframe.H1, ts,
            1.1000m, 1.1010m, 1.0990m, 1.1005m, 1000, 0.0001m);
        var tick = new Tick(symbol, 1.1005m - askBidSpread, 1.1005m + askBidSpread, ts);
        var indicators = new Dictionary<string, double>();
        if (atrPips.HasValue)
            indicators["ATR14"] = atrPips.Value;
        return new MarketContext(symbol, tick,
            new Dictionary<Timeframe, IReadOnlyList<Bar>> { { Timeframe.H1, [bar] } },
            indicators, bar.OpenTimeUtc);
    }

    [Fact]
    public void Allows_WhenSpreadWithinLimit_ReturnsTrue()
    {
        var filter = MakeFilter(2.0m, 0);
        filter.Allows(MakeCtx(0.0001m)).Should().BeTrue();
    }

    [Fact]
    public void Disallows_WhenSpreadExceedsLimit()
    {
        var filter = MakeFilter(1.0m, 0);
        filter.Allows(MakeCtx(0.0003m)).Should().BeFalse();
    }

    [Fact]
    public void Allows_WhenSpreadOk_AndAtrWithinLimit()
    {
        var filter = MakeFilter(2.0m, 30m);
        filter.Allows(MakeCtx(0.0001m, 20.0)).Should().BeTrue();
    }

    [Fact]
    public void Disallows_WhenAtrExceedsLimit()
    {
        var filter = MakeFilter(2.0m, 10m);
        filter.Allows(MakeCtx(0.0001m, 25.0)).Should().BeFalse();
    }

    [Fact]
    public void Allows_WhenAtrMissing_SpreadOk()
    {
        var filter = MakeFilter(2.0m, 5m);
        filter.Allows(MakeCtx(0.0001m, null)).Should().BeTrue();
    }

    [Fact]
    public void Allows_WhenAtrZero_MaxAtrDisabled()
    {
        var filter = MakeFilter(2.0m, 0m);
        filter.Allows(MakeCtx(0.0001m, 15.0)).Should().BeTrue();
    }
}

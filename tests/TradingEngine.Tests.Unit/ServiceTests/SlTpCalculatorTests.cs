namespace TradingEngine.Tests.Unit.ServiceTests;

[Trait("Category", "Services")]
public sealed class SlTpCalculatorTests
{
    private static readonly SymbolInfo EurUsd = new(
        Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private static readonly RiskProfile TestProfile = new(
        "test", "Test", 0.01, 0.05, 0.10, 100.0, 0.05, 0.5, 0.5, 3, false, "ftmo-standard");

    [Fact]
    public void FixedPip_Long_SlBelowEntry()
    {
        var entry = new Price(1.08500m);
        var sl = SlTpHelpers.FixedPip(entry, TradeDirection.Long, new Pips(20), EurUsd);
        sl.Value.Should().Be(1.08300m);
    }

    [Fact]
    public void FixedPip_Short_SlAboveEntry()
    {
        var entry = new Price(1.08500m);
        var sl = SlTpHelpers.FixedPip(entry, TradeDirection.Short, new Pips(20), EurUsd);
        sl.Value.Should().Be(1.08700m);
    }

    [Fact]
    public void AtrBased_RoundsToTickSize()
    {
        var entry = new Price(1.08500m);
        var sl = SlTpHelpers.AtrBased(entry, TradeDirection.Long, 0.0021, 1.5, EurUsd);
        sl.Value.Should().Be(1.08185m);
    }

    [Fact]
    public void RRMultiple_CorrectDistance()
    {
        var entry = new Price(1.08500m);
        var sl = new Price(1.08300m);
        var tp = SlTpHelpers.RRMultiple(entry, sl, TradeDirection.Long, 2.0, EurUsd);
        tp.Should().NotBeNull();
        tp.Value.Value.Should().Be(1.08900m);
    }

    // F71 (iter-structural-edge S1): TakeProfit.Method used to be a dead knob for hand-rolled
    // strategies — they called RRMultiple with opts.RrMultiple directly, so a per-run/pack
    // override to "None" was recorded in the effective config but silently ignored. These pin
    // the Method dispatch the strategies now route through.
    [Fact]
    public void TakeProfitFor_MethodNone_ReturnsNull()
    {
        var opts = new TpOptions { Method = "None", RrMultiple = 2.0 };
        var tp = SlTpHelpers.TakeProfitFor(opts, new Price(1.08500m), new Price(1.08300m), TradeDirection.Long, 0.0021, EurUsd);
        tp.Should().BeNull("Method=None means the position exits via SL/trail/flatten only");
    }

    [Fact]
    public void TakeProfitFor_DefaultRrMultiple_MatchesHistoricalBehavior()
    {
        var opts = new TpOptions { Method = "RrMultiple", RrMultiple = 2.0 };
        var tp = SlTpHelpers.TakeProfitFor(opts, new Price(1.08500m), new Price(1.08300m), TradeDirection.Long, 0.0021, EurUsd);
        tp.Should().Be(SlTpHelpers.RRMultiple(new Price(1.08500m), new Price(1.08300m), TradeDirection.Long, 2.0, EurUsd),
            "every existing config uses RrMultiple — the fix must be behavior-preserving for them");
    }

    [Fact]
    public void TakeProfitFor_AtrMultiple_UsesAtrDistance()
    {
        var opts = new TpOptions { Method = "AtrMultiple", AtrMultiple = 2.0 };
        var tp = SlTpHelpers.TakeProfitFor(opts, new Price(1.08500m), new Price(1.08300m), TradeDirection.Long, 0.0021, EurUsd);
        tp.Should().Be(SlTpHelpers.AtrMultiple(new Price(1.08500m), TradeDirection.Long, 0.0021, 2.0, EurUsd));
    }

    [Fact]
    public void IsSlValid_ReturnsTrue_ForValidSl()
    {
        var entry = new Price(1.08500m);
        var sl = new Price(1.08300m);
        var valid = SlTpHelpers.IsSlValid(entry, sl, TradeDirection.Long, EurUsd, TestProfile);
        valid.Should().BeTrue();
    }

    [Fact]
    public void IsSlValid_ReturnsFalse_WhenSlAboveEntryForLong()
    {
        var entry = new Price(1.08500m);
        var sl = new Price(1.08700m);
        var valid = SlTpHelpers.IsSlValid(entry, sl, TradeDirection.Long, EurUsd, TestProfile);
        valid.Should().BeFalse();
    }
}

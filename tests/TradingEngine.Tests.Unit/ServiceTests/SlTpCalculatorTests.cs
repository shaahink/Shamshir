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

using TradingEngine.Engine;

namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Engine")]
[Trait("Speed", "Fast")]
public sealed class PositionLifecycleTrailingTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");
    private static readonly SymbolInfo EurusdInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private static PositionState CreateLong(decimal entry = 1.0850m, decimal sl = 1.0821m)
    {
        return new PositionState(
            Guid.NewGuid(), Guid.NewGuid(), Eurusd, TradeDirection.Long, 0.1m,
            new Price(entry), new Price(sl), null, DateTime.UtcNow, "test",
            PositionPhase.Open, 0.1m);
    }

    private static PositionState CreateShort(decimal entry = 1.0850m, decimal sl = 1.0879m)
    {
        return new PositionState(
            Guid.NewGuid(), Guid.NewGuid(), Eurusd, TradeDirection.Short, 0.1m,
            new Price(entry), new Price(sl), null, DateTime.UtcNow, "test",
            PositionPhase.Open, 0.1m);
    }

    [Fact]
    public void TrailStepPips_Long_MovesSlUp_WhenBidAdvancesEnough()
    {
        var state = CreateLong();
        var result = PositionLifecycle.TrailStepPips(state, 1.0865m, 1.0867m, new Pips(10), EurusdInfo);

        result.Should().NotBeNull();
        result!.Value.Value.Should().BeGreaterThan(1.0821m);
    }

    [Fact]
    public void TrailStepPips_Long_ReturnsNull_WhenBidNotAdvanced()
    {
        var state = CreateLong();
        var result = PositionLifecycle.TrailStepPips(state, 1.0820m, 1.0822m, new Pips(10), EurusdInfo);

        result.Should().BeNull();
    }

    [Fact]
    public void TrailStepPips_Short_MovesSlDown_WhenAskDropsEnough()
    {
        var state = CreateShort();
        var result = PositionLifecycle.TrailStepPips(state, 1.0830m, 1.0835m, new Pips(10), EurusdInfo);

        result.Should().NotBeNull();
        result!.Value.Value.Should().BeLessThan(1.0879m);
    }

    [Fact]
    public void TrailAtr_Long_UsesHighWater()
    {
        var state = CreateLong();
        var result = PositionLifecycle.TrailAtr(state, 1.0870m, 1.0800m, 0.0010, 2.0, EurusdInfo);

        result.Should().NotBeNull();
        result!.Value.Value.Should().Be(1.0850m);
    }

    [Fact]
    public void TrailAtr_Long_ReturnsNull_WhenNewSlNotBetter()
    {
        var state = CreateLong();
        var result = PositionLifecycle.TrailAtr(state, 1.0830m, 1.0800m, 0.0010, 2.0, EurusdInfo);

        result.Should().BeNull();
    }

    [Fact]
    public void TryBreakeven_Long_FiresWhenPriceReachesTriggerR()
    {
        var state = CreateLong(1.0850m, 1.0821m);
        var result = PositionLifecycle.TryBreakeven(state, 1.0880m, 1.0882m, 1.0, new Pips(1), EurusdInfo);

        result.Should().NotBeNull();
    }

    [Fact]
    public void TryBreakeven_Long_ReturnsNull_BeforeTriggerR()
    {
        var state = CreateLong(1.0850m, 1.0821m);
        var result = PositionLifecycle.TryBreakeven(state, 1.0855m, 1.0857m, 1.0, new Pips(1), EurusdInfo);

        result.Should().BeNull();
    }

    [Fact]
    public void TrailStructure_Long_FindsSwingLow()
    {
        var state = CreateLong(1.0850m, 1.0821m);
        var bars = new List<Bar>
        {
            new(Eurusd, Timeframe.H1, DateTime.UtcNow.AddHours(-10), 1.0860m, 1.0870m, 1.0840m, 1.0850m, 1000),
            new(Eurusd, Timeframe.H1, DateTime.UtcNow.AddHours(-9), 1.0860m, 1.0870m, 1.0840m, 1.0860m, 1000),
            new(Eurusd, Timeframe.H1, DateTime.UtcNow.AddHours(-8), 1.0860m, 1.0870m, 1.0830m, 1.0860m, 1000),
            new(Eurusd, Timeframe.H1, DateTime.UtcNow.AddHours(-7), 1.0860m, 1.0870m, 1.0845m, 1.0865m, 1000),
            new(Eurusd, Timeframe.H1, DateTime.UtcNow.AddHours(-6), 1.0860m, 1.0870m, 1.0840m, 1.0855m, 1000),
        };

        var result = PositionLifecycle.TrailStructure(state, bars, 3, 0.0005, 1.5, EurusdInfo);

        result.Should().NotBeNull();
    }

    [Fact]
    public void TrailSteppedR_Long_MovesToEntry_AtFirstR()
    {
        var state = CreateLong(1.0850m, 1.0821m) with { InitialSlDistance = 0.0029m };
        var result = PositionLifecycle.TrailSteppedR(state, 1.0879m, 1.0881m, [1.0, 2.0, 3.0], EurusdInfo);

        result.Should().NotBeNull();
        result!.Value.Value.Should().Be(1.0850m);
    }

    [Fact]
    public void TrailSteppedR_Long_ReturnsNull_BeforeFirstR()
    {
        var state = CreateLong(1.0850m, 1.0821m) with { InitialSlDistance = 0.0029m };
        var result = PositionLifecycle.TrailSteppedR(state, 1.0845m, 1.0847m, [1.0, 2.0, 3.0], EurusdInfo);

        result.Should().BeNull();
    }
}

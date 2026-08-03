using TradingEngine.Services.Helpers;

namespace TradingEngine.Tests.Unit.Phase31Tests;

/// <summary>
/// F87 P4: one resolver decides which spread NUMBER every consumer reads — recorded per-bar value
/// when present, else the registry's TypicalSpread, else the caller's fallback. Offset conventions
/// (full-spread ask on fills/floating PnL, half-spread on synthesized strategy ticks) stay at the
/// call sites (R3); this resolves only the number.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SpreadResolverTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");

    private static ISymbolInfoRegistry Registry(decimal typicalSpread)
    {
        var info = new SymbolInfo(Eurusd, SymbolCategory.Forex, "EUR", "USD",
            0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, typicalSpread);
        var registry = Substitute.For<ISymbolInfoRegistry>();
        registry.Get(Eurusd).Returns(info);
        return registry;
    }

    [Fact]
    public void RecordedPerBarSpread_WinsOverRegistry()
    {
        SpreadResolver.FullSpread(0.00015m, Registry(0.0002m), Eurusd, fallback: 0.0001m)
            .Should().Be(0.00015m);
    }

    [Fact]
    public void NoRecordedSpread_FallsBackToRegistryTypicalSpread()
    {
        SpreadResolver.FullSpread(null, Registry(0.0002m), Eurusd, fallback: 0.0001m)
            .Should().Be(0.0002m);
    }

    [Fact]
    public void RegistryMiss_UsesCallerFallback()
    {
        var registry = Substitute.For<ISymbolInfoRegistry>();
        registry.Get(Eurusd).Returns(_ => throw new KeyNotFoundException());

        SpreadResolver.FullSpread(null, registry, Eurusd, fallback: 0.001m)
            .Should().Be(0.001m, "each call site keeps its own historical fallback");
    }

    [Fact]
    public void RecordedZero_IsARealValue_NotTreatedAsUnset()
    {
        // A captured zero-spread bar (e.g. a raw-tick artifact) is data, not absence.
        SpreadResolver.FullSpread(0m, Registry(0.0002m), Eurusd, fallback: 0.0001m)
            .Should().Be(0m);
    }
}

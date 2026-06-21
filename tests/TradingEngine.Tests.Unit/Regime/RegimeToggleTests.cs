namespace TradingEngine.Tests.Unit.Regime;

/// <summary>
/// iter-38 R1 / D3: the regime filter is a no-op when detection is disabled. The per-strategy switch is the
/// <see cref="RegimeFilterOptions.DetectionEnabled"/> flag (short-circuits <c>Allows</c> to allow-all); the
/// run-level master (BarEvaluator's disableRegime ⇒ StrategyBank.GetActive ignoreRegime) bypasses the filter
/// entirely. Detection ON keeps the per-regime flags authoritative (golden path unchanged).
/// </summary>
[Trait("Category", "Regime")]
[Trait("Speed", "Fast")]
public sealed class RegimeToggleTests
{
    [Fact]
    public void Detection_off_allows_a_regime_the_filter_would_block()
    {
        var filter = new RegimeFilterOptions { AllowRanging = false, DetectionEnabled = false };
        filter.Allows(MarketRegime.Ranging).Should().BeTrue();
    }

    [Fact]
    public void Detection_on_enforces_per_regime_flags()
    {
        var filter = new RegimeFilterOptions { AllowRanging = false };
        filter.Allows(MarketRegime.Ranging).Should().BeFalse();
        filter.Allows(MarketRegime.Trending).Should().BeTrue();
    }
}

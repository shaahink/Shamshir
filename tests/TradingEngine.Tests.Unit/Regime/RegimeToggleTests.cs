namespace TradingEngine.Tests.Unit.Regime;

/// <summary>
/// iter-38 R1 / D3: the regime filter is a no-op when detection is disabled. <see cref="RegimeFilterOptions.DetectionEnabled"/>
/// is the SINGLE source of truth — the per-strategy default, a pack's <c>RegimeDetectionEnabled</c>, and the
/// run-level master (<c>StartRunRequest.DisableRegime</c>, folded onto every strategy by the orchestrator) all
/// converge on it, and <c>StrategyBankService.GetActive</c> consumes it through <c>RegimeFilter.Allows</c> (which
/// short-circuits to allow-all when detection is off). Detection ON keeps the per-regime flags authoritative
/// (golden path unchanged). There is no separate run-level switch in the evaluator (the old dead
/// <c>BarEvaluator.disableRegime</c> param was removed in the iter-39 follow-up).
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

    [Fact]
    public void Detection_off_allows_every_regime_regardless_of_per_regime_flags()
    {
        // This is exactly the state the run-master DisableRegime (and a regime-off pack) produces on every
        // strategy: detection off, all per-regime flags false. The strategy must still trade in every regime.
        var filter = new RegimeFilterOptions
        {
            DetectionEnabled = false,
            AllowTrending = false,
            AllowRanging = false,
            AllowHighVolatility = false,
            AllowLowVolatility = false,
            AllowUnknown = false,
        };

        foreach (var regime in Enum.GetValues<MarketRegime>())
            filter.Allows(regime).Should().BeTrue($"detection-off must allow {regime} regardless of the per-regime flags");
    }
}

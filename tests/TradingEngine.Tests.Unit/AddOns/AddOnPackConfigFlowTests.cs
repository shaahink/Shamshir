namespace TradingEngine.Tests.Unit.AddOns;

/// <summary>
/// iter-38 PK2/PK3 (run-wiring seam): proves the chain the <c>BacktestOrchestrator</c> relies on to make a pack
/// actually change engine behaviour — <c>ApplyPack(strategyAddOns, pack)</c> → <c>PositionManager.BuildConfig</c>
/// → a runtime <c>PositionManagementConfig</c> whose trailing/partial reflect the PACK, not the strategy default.
/// The pure <c>PackResolutionTests</c> and the kernel <c>PartialTpKernelTests</c> bracket this seam but never join
/// it; without this test the pack→config plumbing (the iter-38 integration most likely to hide a bug) had no
/// coverage. Also pins ApplyPack determinism, which the run identity (ConfigSetId / K6 replay) depends on.
/// </summary>
[Trait("Category", "AddOns")]
[Trait("Speed", "Fast")]
public sealed class AddOnPackConfigFlowTests
{
    private readonly EffectiveConfigResolver _resolver = new();

    [Fact]
    public void Pack_enables_trailing_on_a_strategy_that_had_none()
    {
        var strategy = new PositionManagementOptions { Trailing = new TrailingOptions { Enabled = false } };
        var pack = new AddOnPack("runner", "Runner", null,
            new PositionManagementOptions
            {
                Trailing = new TrailingOptions { Enabled = true, Mode = AddOnMode.Custom, Method = "AtrMultiple", AtrMultiple = 3.0 },
            });

        var resolved = _resolver.ApplyPack(strategy, pack)!;
        var config = PositionManager.BuildConfig("s", resolved, 0m);

        config.TrailingStop.Method.Should().Be(TrailingMethod.AtrMultiple, "the pack's trailing must reach the runtime config");
        config.TrailingStop.AtrMultiple.Should().Be(3.0);
    }

    [Fact]
    public void Pack_enables_partialtp_on_a_strategy_that_had_none()
    {
        var strategy = new PositionManagementOptions { PartialTp = null };
        var pack = new AddOnPack("scalp", "Scalp", null,
            new PositionManagementOptions
            {
                PartialTp = new PartialTpOptions { Enabled = true, Mode = AddOnMode.Custom, TriggerRMultiple = 1.5, CloseFraction = 0.4 },
            });

        var config = PositionManager.BuildConfig("s", _resolver.ApplyPack(strategy, pack)!, 0m);

        config.PartialTpEnabled.Should().BeTrue();
        config.PartialTpTriggerR.Should().Be(1.5);
        config.PartialTpCloseFraction.Should().Be(0.4);
    }

    [Fact]
    public void ApplyPack_is_deterministic()
    {
        var strategy = new PositionManagementOptions { Trailing = new TrailingOptions { Enabled = false } };
        var pack = new AddOnPack("p", "P", null,
            new PositionManagementOptions
            {
                Trailing = new TrailingOptions { Enabled = true, Mode = AddOnMode.Custom, Method = "AtrMultiple", AtrMultiple = 2.2 },
            });

        var a = _resolver.ApplyPack(strategy, pack);
        var b = _resolver.ApplyPack(strategy, pack);

        a.Should().BeEquivalentTo(b,
            "identical (strategy, pack) inputs must resolve to an identical config so the run identity (ConfigSetId) is stable — K6");
    }
}

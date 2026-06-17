using System.Text.Json;
using FluentAssertions;

namespace TradingEngine.Tests.Unit.Phase32Tests;

[Trait("Category", "Services")]
public sealed class EffectiveConfigResolverTests
{
    private readonly EffectiveConfigResolver _resolver = new();

    private static StrategyConfigEntry DefaultConfig() => new(
        Id: "trend-breakout",
        DisplayName: "Trend Breakout",
        Enabled: true,
        Symbols: ["EURUSD", "GBPUSD"],
        RiskProfileId: "standard",
        Parameters: JsonDocument.Parse("""{"atrPeriod": 14, "atrMultiplier": 2.0}""").RootElement,
        Timeframe: "H1")
    {
        PositionManagement = new PositionManagementOptions
        {
            StopLoss = new SlOptions { Method = "AtrMultiple", AtrMultiple = 1.5, MaxPips = 100 },
            TakeProfit = new TpOptions { Method = "RrMultiple", RrMultiple = 2.0 },
            Breakeven = new BreakevenOptions { Enabled = true, TriggerRMultiple = 1.0 },
            Trailing = new TrailingOptions { Method = "None" },
        },
        OrderEntry = new OrderEntryOptions { Method = OrderEntryMethod.Market, MaxSlippagePips = 2.0 },
        RegimeFilter = new RegimeFilterOptions { AllowTrending = true, AllowRanging = true },
        Reentry = new ReentryOptions { BlockWhileSameDirectionOpen = true, CooldownBarsAfterSl = 5 },
    };

    [Fact]
    public void Overriding_only_tp_leaves_all_other_fields_inherited()
    {
        var stored = DefaultConfig();
        var ovr = new StrategyOverride
        {
            PositionManagement = new PositionManagementOptions
            {
                TakeProfit = new TpOptions { RrMultiple = 3.0 },
            },
        };

        var result = _resolver.Resolve(stored, ovr, null);

        // Overridden
        result.PositionManagement!.TakeProfit.RrMultiple.Should().Be(3.0);

        // Inherited — SL stays as stored
        result.PositionManagement.StopLoss.Method.Should().Be("AtrMultiple");
        result.PositionManagement.StopLoss.AtrMultiple.Should().Be(1.5);
        result.PositionManagement.StopLoss.MaxPips.Should().Be(100);

        // Inherited — Breakeven stays
        result.PositionManagement.Breakeven.Enabled.Should().BeTrue();
        result.PositionManagement.Breakeven.TriggerRMultiple.Should().Be(1.0);

        // Inherited — Trailing stays
        result.PositionManagement.Trailing.Method.Should().Be("None");

        // Inherited — top-level fields
        result.Id.Should().Be("trend-breakout");
        result.DisplayName.Should().Be("Trend Breakout");
        result.Enabled.Should().BeTrue();
        result.RiskProfileId.Should().Be("standard");
        result.Timeframe.Should().Be("H1");
        result.Symbols.Should().Equal("EURUSD", "GBPUSD");

        // Inherited — OrderEntry
        result.OrderEntry!.Method.Should().Be(OrderEntryMethod.Market);
        result.OrderEntry.MaxSlippagePips.Should().Be(2.0);

        // Inherited — RegimeFilter
        result.RegimeFilter!.AllowTrending.Should().BeTrue();
        result.RegimeFilter.AllowRanging.Should().BeTrue();

        // Inherited — Reentry
        result.Reentry!.BlockWhileSameDirectionOpen.Should().BeTrue();
        result.Reentry.CooldownBarsAfterSl.Should().Be(5);
    }

    [Fact]
    public void Two_runs_with_different_overrides_yield_different_effective_configs()
    {
        var stored = DefaultConfig();

        var ovrA = new StrategyOverride
        {
            PositionManagement = new PositionManagementOptions
            {
                TakeProfit = new TpOptions { RrMultiple = 3.0, Method = "FixedPips" },
            },
        };

        var ovrB = new StrategyOverride
        {
            RiskProfileId = "aggressive",
            PositionManagement = new PositionManagementOptions
            {
                StopLoss = new SlOptions { AtrMultiple = 2.0 },
            },
        };

        var resultA = _resolver.Resolve(stored, ovrA, null);
        var resultB = _resolver.Resolve(stored, ovrB, null);

        // Different TP
        resultA.PositionManagement!.TakeProfit.RrMultiple.Should().Be(3.0);
        resultB.PositionManagement!.TakeProfit.RrMultiple.Should().Be(2.0); // inherited default

        // Different RiskProfileId
        resultA.RiskProfileId.Should().Be("standard");
        resultB.RiskProfileId.Should().Be("aggressive");

        // Different SL
        resultA.PositionManagement.StopLoss.AtrMultiple.Should().Be(1.5);
        resultB.PositionManagement.StopLoss.AtrMultiple.Should().Be(2.0);

        // The two configs should differ
        resultA.Should().NotBeEquivalentTo(resultB);
    }

    [Fact]
    public void Stored_default_is_unchanged_after_resolving()
    {
        var stored = DefaultConfig();
        var originalSlMethod = stored.PositionManagement!.StopLoss.Method;
        var originalTpRr = stored.PositionManagement.TakeProfit.RrMultiple;
        var originalRfId = stored.RiskProfileId;

        var ovr = new StrategyOverride
        {
            PositionManagement = new PositionManagementOptions
            {
                StopLoss = new SlOptions { Method = "FixedPips" },
                TakeProfit = new TpOptions { RrMultiple = 4.0 },
            },
            RiskProfileId = "conservative",
        };

        _ = _resolver.Resolve(stored, ovr, null);

        // Stored config must not be mutated
        stored.PositionManagement.StopLoss.Method.Should().Be(originalSlMethod);
        stored.PositionManagement.TakeProfit.RrMultiple.Should().Be(originalTpRr);
        stored.RiskProfileId.Should().Be(originalRfId);
    }

    [Fact]
    public void Null_override_returns_stored_default_unchanged()
    {
        var stored = DefaultConfig();
        var result = _resolver.Resolve(stored, null, null);

        result.Id.Should().Be(stored.Id);
        result.DisplayName.Should().Be(stored.DisplayName);
        result.Enabled.Should().Be(stored.Enabled);
        result.RiskProfileId.Should().Be(stored.RiskProfileId);
        result.Timeframe.Should().Be(stored.Timeframe);
        result.Symbols.Should().Equal(stored.Symbols);
        result.PositionManagement!.StopLoss.Method.Should().Be(stored.PositionManagement!.StopLoss.Method);
    }

    [Fact]
    public void SymbolTimeframePair_overrides_symbols()
    {
        var stored = DefaultConfig();
        var plan = new SymbolTimeframePair("USDJPY", "M15");

        var result = _resolver.Resolve(stored, null, plan);

        result.Symbols.Should().Equal("USDJPY");
        result.Timeframe.Should().Be("M15");
    }

    [Fact]
    public void PerRunOverride_parameters_merge_with_stored()
    {
        var stored = DefaultConfig();
        var overrideParams = JsonDocument.Parse("""{"atrMultiplier": 3.5, "newParam": true}""").RootElement;
        var ovr = new StrategyOverride { Parameters = overrideParams };

        var result = _resolver.Resolve(stored, ovr, null);

        result.Parameters.TryGetProperty("atrPeriod", out var atrPeriod).Should().BeTrue();
        atrPeriod.GetInt32().Should().Be(14); // inherited
        result.Parameters.TryGetProperty("atrMultiplier", out var atrMult).Should().BeTrue();
        atrMult.GetDouble().Should().Be(3.5); // overridden
        result.Parameters.TryGetProperty("newParam", out var newParam).Should().BeTrue();
        newParam.GetBoolean().Should().BeTrue(); // added
    }

    [Fact]
    public void StrategyOverride_can_change_StrategyId_and_Enabled()
    {
        var stored = DefaultConfig();
        var ovr = new StrategyOverride { StrategyId = "custom-id", Enabled = false };

        var result = _resolver.Resolve(stored, ovr, null);

        result.Id.Should().Be("custom-id");
        result.Enabled.Should().BeFalse();
    }
}

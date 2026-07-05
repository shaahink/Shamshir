namespace TradingEngine.Tests.Unit.Services;

/// <summary>
/// iter-quant-model P2.6 (D9, units doctrine). <see cref="UnitConversion"/> is the ONE place a normalized
/// unit (ATR-multiple, spread-multiple, ATR-fraction) turns into a concrete pip number for a given
/// (symbol, timeframe) — everything downstream (SlTpResolver, PreTradeGate, EntryPlanner, PositionManager)
/// keeps reading the existing raw-pip fields unchanged. The whole point: the SAME multiple produces a
/// sensible pip distance on EURUSD H1 and on XAUUSD H1, where a flat pip cap was silently wrong for gold.
/// </summary>
public sealed class UnitConversionTests
{
    private static readonly SymbolInfo EurUsd = new(
        new Symbol("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
        PipSize: 0.0001m, TickSize: 0.00001m, ContractSize: 100000, MinLots: 0.01m, MaxLots: 100m,
        LotStep: 0.01m, MarginRate: 0.03333m, TypicalSpread: 0.0001m);

    private static readonly SymbolInfo XauUsd = new(
        new Symbol("XAUUSD"), SymbolCategory.Metal, "XAU", "USD",
        PipSize: 0.01m, TickSize: 0.001m, ContractSize: 100, MinLots: 0.01m, MaxLots: 10m,
        LotStep: 0.01m, MarginRate: 0.05m, TypicalSpread: 0.3m);

    // EURUSD H1 reference: spreadPips = 0.0001/0.0001 = 1.0; ReferenceAtrPips(H1) = 1.0 * 20 = 20.
    // XAUUSD H1 reference: spreadPips = 0.3/0.01 = 30.0; ReferenceAtrPips(H1) = 30.0 * 20 = 600.

    [Fact]
    public void ReferenceAtrPips_ScalesWithSymbol_NotFlat()
    {
        UnitConversion.ReferenceAtrPips(Timeframe.H1, EurUsd).Should().Be(20.0);
        UnitConversion.ReferenceAtrPips(Timeframe.H1, XauUsd).Should().Be(600.0);
    }

    [Fact]
    public void ResolvePips_PositionManagement_MaxSlAtrMultipleSet_OverridesMaxPips_PerSymbol()
    {
        var pm = new PositionManagementOptions
        {
            StopLoss = new SlOptions { MaxPips = 100, MaxSlAtrMultiple = 5.0 },
        };

        var eurResolved = pm.ResolvePips(Timeframe.H1, EurUsd);
        var xauResolved = pm.ResolvePips(Timeframe.H1, XauUsd);

        // A flat 100-pip cap would crush XAUUSD (whose natural ATR is ~600 pips); the multiple scales it.
        eurResolved.StopLoss.MaxPips.Should().Be(100.0);   // 5.0 * 20
        xauResolved.StopLoss.MaxPips.Should().Be(3000.0);  // 5.0 * 600 — NOT still 100.
    }

    [Fact]
    public void ResolvePips_PositionManagement_MultipleAbsent_LeavesRawPipsUnchanged()
    {
        var pm = new PositionManagementOptions
        {
            StopLoss = new SlOptions { MaxPips = 42 },
            Breakeven = new BreakevenOptions { OffsetPips = 7 },
            Trailing = new TrailingOptions { StepPips = 11 },
        };

        var resolved = pm.ResolvePips(Timeframe.H1, XauUsd);

        resolved.StopLoss.MaxPips.Should().Be(42);
        resolved.Breakeven.OffsetPips.Should().Be(7);
        resolved.Trailing.StepPips.Should().Be(11);
    }

    [Fact]
    public void ResolvePips_PositionManagement_OffsetSpreadMultipleSet_OverridesOffsetPips_PerSymbol()
    {
        var pm = new PositionManagementOptions
        {
            Breakeven = new BreakevenOptions { OffsetPips = 1.0, OffsetSpreadMultiple = 2.0 },
        };

        var eurResolved = pm.ResolvePips(Timeframe.H1, EurUsd);
        var xauResolved = pm.ResolvePips(Timeframe.H1, XauUsd);

        eurResolved.Breakeven.OffsetPips.Should().Be(2.0);   // 2.0 * spreadPips(1.0)
        xauResolved.Breakeven.OffsetPips.Should().Be(60.0);  // 2.0 * spreadPips(30.0)
    }

    [Fact]
    public void ResolvePips_PositionManagement_StepAtrFractionSet_OverridesStepPips_PerSymbol()
    {
        var pm = new PositionManagementOptions
        {
            Trailing = new TrailingOptions { StepPips = 10, StepAtrFraction = 0.5 },
        };

        var xauResolved = pm.ResolvePips(Timeframe.H1, XauUsd);

        xauResolved.Trailing.StepPips.Should().Be(300.0); // 0.5 * 600
    }

    [Fact]
    public void ResolvePips_OrderEntry_LimitOffsetAtrFractionSet_OverridesLimitOffsetPips_PerSymbol()
    {
        var oe = new OrderEntryOptions { LimitOffsetPips = 5.0, LimitOffsetAtrFraction = 0.25 };

        var eurResolved = oe.ResolvePips(Timeframe.H1, EurUsd);
        var xauResolved = oe.ResolvePips(Timeframe.H1, XauUsd);

        eurResolved.LimitOffsetPips.Should().Be(5.0);    // 0.25 * 20
        xauResolved.LimitOffsetPips.Should().Be(150.0);  // 0.25 * 600
    }

    [Fact]
    public void ResolvePips_OrderEntry_MaxSlippageSpreadMultipleSet_OverridesMaxSlippagePips_PerSymbol()
    {
        var oe = new OrderEntryOptions { MaxSlippagePips = 2.0, MaxSlippageSpreadMultiple = 2.0 };

        var eurResolved = oe.ResolvePips(Timeframe.H1, EurUsd);
        var xauResolved = oe.ResolvePips(Timeframe.H1, XauUsd);

        eurResolved.MaxSlippagePips.Should().Be(2.0);   // 2.0 * spreadPips(1.0)
        xauResolved.MaxSlippagePips.Should().Be(60.0);  // 2.0 * spreadPips(30.0)
    }

    [Fact]
    public void ResolvePips_OrderEntry_MultipleAbsent_LeavesRawPipsUnchanged()
    {
        var oe = new OrderEntryOptions { LimitOffsetPips = 3.0, MaxSlippagePips = 1.5 };

        var resolved = oe.ResolvePips(Timeframe.H1, XauUsd);

        resolved.LimitOffsetPips.Should().Be(3.0);
        resolved.MaxSlippagePips.Should().Be(1.5);
    }

    [Fact]
    public void ResolveMaxSlPips_RiskProfile_MultipleSet_OverridesMaxSlPips_PerSymbol()
    {
        var profile = MakeProfile(maxSlPips: 100, maxSlAtrMultiple: 5.0);

        var eurResolved = profile.ResolveMaxSlPips(Timeframe.H1, EurUsd);
        var xauResolved = profile.ResolveMaxSlPips(Timeframe.H1, XauUsd);

        eurResolved.MaxSlPips.Should().Be(100.0);
        xauResolved.MaxSlPips.Should().Be(3000.0); // the actual gold/BTC bug this fixes
    }

    [Fact]
    public void ResolveMaxSlPips_RiskProfile_MultipleAbsent_LeavesRawPipsUnchanged()
    {
        var profile = MakeProfile(maxSlPips: 100, maxSlAtrMultiple: null);

        var resolved = profile.ResolveMaxSlPips(Timeframe.H1, XauUsd);

        resolved.MaxSlPips.Should().Be(100.0);
    }

    [Fact]
    public void ResolvePips_ZeroReferenceScale_LeavesRawPipsUnchanged_DoesNotZeroOut()
    {
        // A symbol with no typical spread configured (registry gap) must never silently zero-out a
        // real limit via 0-times-multiple — fall back to the raw pips instead.
        var zeroSpreadSymbol = EurUsd with { TypicalSpread = 0m };
        var pm = new PositionManagementOptions
        {
            StopLoss = new SlOptions { MaxPips = 100, MaxSlAtrMultiple = 5.0 },
        };

        var resolved = pm.ResolvePips(Timeframe.H1, zeroSpreadSymbol);

        resolved.StopLoss.MaxPips.Should().Be(100.0);
    }

    private static RiskProfile MakeProfile(double maxSlPips, double? maxSlAtrMultiple) => new(
        "test", "Test", RiskPerTradePercent: 0.005, MaxDailyDrawdownPercent: 0.04,
        MaxTotalDrawdownPercent: 0.08, MaxSlPips: maxSlPips, MaxExposurePercent: 0.05,
        DrawdownScaleThreshold: 0.5, DrawdownScaleFloor: 0.5, MaxConcurrentPositions: 3,
        AllowHedging: false, PropFirmRuleSetId: "ftmo-standard")
    {
        MaxSlAtrMultiple = maxSlAtrMultiple,
    };
}

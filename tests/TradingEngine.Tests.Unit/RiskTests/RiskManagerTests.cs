namespace TradingEngine.Tests.Unit.RiskTests;

[Trait("Category", "Risk")]
public sealed class RiskManagerTests
{
    private static readonly PropFirmRuleSet FtmoRules = new(
        "ftmo-standard", "FTMO Standard", "Fixed", 0.05, 0.10, 0.10, 4,
        "BalancePlusFloatingMinusFeesAndSwaps", "22:00:00", "Europe/Prague",
        false, "High", 30, 15, false, "21:00:00", "20:00:00", "NextTradingDay", false);

    private static readonly RiskProfile Profile = new(
        "standard", "Standard", 0.01, 0.04, 0.08, 100, 0.05, 0.5, 0.5, 5, false, "ftmo-standard");

    private static RiskManager MakeRm(decimal initialBalance = 100_000)
    {
        var registry = new SymbolInfoRegistry();
        registry.Register(EurUsd());
        var gov = Substitute.For<ITradingGovernor>();
        gov.Evaluate(Arg.Any<GovernorContext>())
            .Returns(new GovernorDecision(true, 1.0m, GovernorTradingState.Normal, "OK"));
        var rm = new RiskManager(registry, (_, _) => 1m,
            new NewsFilter(), new SessionFilter(), new StubClock(DateTime.UtcNow),
            Substitute.For<ICurrencyExposureTracker>(), gov, new SizingPolicyOptions());
        rm.InitializeDrawdownIfNeeded(initialBalance);
        rm.SetActiveRuleSet(FtmoRules);
        rm.SetConstraints(ConstraintSet.Resolve(Profile, FtmoRules));
        return rm;
    }

    private static SymbolInfo EurUsd() => new(
        Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private static EquitySnapshot Snapshot(decimal equity, decimal dailyDd = 0m, decimal maxDd = 0m) =>
        new(DateTime.UtcNow, equity, 0, equity, equity, equity, dailyDd, maxDd, EngineMode.Backtest);

    private static TradeIntent LongEurUsd() => new(
        Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
        new Price(1.08210m), new Price(1.08500m),
        "strat-1", "standard", "Test signal", DateTime.UtcNow);

    [Fact]
    public void Validate_DailyDD_JustBeforeLimit_Allows()
    {
        var rm = MakeRm();
        var snap = Snapshot(100_000, dailyDd: 0.0499m);
        var v = rm.Validate(LongEurUsd(), snap, Profile, 1.1000m);
        v.Should().NotContain(x => x.Code == "DAILY_DD_LIMIT");
    }

    [Fact]
    public void Validate_DailyDD_AtLimit_Blocks()
    {
        var rm = MakeRm();
        var snap = Snapshot(100_000, dailyDd: 0.05m);
        var v = rm.Validate(LongEurUsd(), snap, Profile, 1.1000m);
        v.Should().Contain(x => x.Code == "DAILY_DD_LIMIT");
    }

    [Fact]
    public void Validate_DailyDD_ExceedsLimit_Blocks()
    {
        var rm = MakeRm();
        var snap = Snapshot(100_000, dailyDd: 0.072m);
        var v = rm.Validate(LongEurUsd(), snap, Profile, 1.1000m);
        v.Should().Contain(x => x.Code == "DAILY_DD_LIMIT");
    }

    [Fact]
    public void Validate_MaxDD_JustBeforeLimit_Allows()
    {
        var rm = MakeRm();
        var snap = Snapshot(100_000, maxDd: 0.0999m);
        var v = rm.Validate(LongEurUsd(), snap, Profile, 1.1000m);
        v.Should().NotContain(x => x.Code == "MAX_DD_LIMIT");
    }

    [Fact]
    public void Validate_MaxDD_AtLimit_Blocks()
    {
        var rm = MakeRm();
        var snap = Snapshot(100_000, maxDd: 0.10m);
        var v = rm.Validate(LongEurUsd(), snap, Profile, 1.1000m);
        v.Should().Contain(x => x.Code == "MAX_DD_LIMIT");
    }

    [Fact]
    public void Validate_ProtectionModeActive_BlocksEvenWithZeroDd()
    {
        var rm = MakeRm();
        rm.EnterProtectionMode("Manual halt", ProtectionCause.MaxDrawdown);
        var snap = Snapshot(100_000, dailyDd: 0m, maxDd: 0m);
        var v = rm.Validate(LongEurUsd(), snap, Profile, 1.1000m);
        v.Should().Contain(x => x.Code == "PROTECTION_MODE_ACTIVE");
    }

    [Fact]
    public void OnDailyReset_FromDailyDDProtection_ResumesTrading()
    {
        var rm = MakeRm();
        rm.EnterProtectionMode("Daily DD breach", ProtectionCause.DailyDrawdown);
        rm.OnDailyReset(100_000);
        rm.CurrentState.InProtectionMode.Should().BeFalse();
        rm.CurrentState.TradingAllowed.Should().BeTrue();
    }

    [Fact]
    public void OnDailyReset_FromMaxDDProtection_StaysSuspended()
    {
        var rm = MakeRm();
        rm.EnterProtectionMode("Max DD breach", ProtectionCause.MaxDrawdown);
        rm.OnDailyReset(100_000);
        rm.CurrentState.InProtectionMode.Should().BeTrue();
    }

    [Fact]
    public void Validate_AfterRegisteringPositions_CountsCorrectly()
    {
        var rm = MakeRm();
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);

        var v = rm.Validate(LongEurUsd(), Snapshot(100_000), Profile, 1.1000m);
        v.Should().Contain(x => x.Code == "MAX_POSITIONS");
    }

    [Fact]
    public void Validate_AfterDeregisteringPosition_AllowsAgain()
    {
        var rm = MakeRm();
        var id1 = Guid.NewGuid();
        rm.RegisterPosition(id1, "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);
        rm.RegisterPosition(Guid.NewGuid(), "strat-1", 50m);

        rm.DeregisterPosition(id1);
        var v = rm.Validate(LongEurUsd(), Snapshot(100_000), Profile, 1.1000m);
        v.Should().NotContain(x => x.Code == "MAX_POSITIONS");
    }

    [Fact]
    public void Circuit_LossUpdatesTracker_SnapshotBlocksNextTrade()
    {
        var registry = new SymbolInfoRegistry();
        registry.Register(EurUsd());
        var gov = Substitute.For<ITradingGovernor>();
        gov.Evaluate(Arg.Any<GovernorContext>())
            .Returns(new GovernorDecision(true, 1.0m, GovernorTradingState.Normal, "OK"));
        var rm = new RiskManager(registry, (_, _) => 1m,
            new NewsFilter(), new SessionFilter(), new StubClock(DateTime.UtcNow),
            Substitute.For<ICurrencyExposureTracker>(), gov, new SizingPolicyOptions());
        rm.InitializeDrawdownIfNeeded(100_000m);
        rm.SetActiveRuleSet(FtmoRules);
        rm.SetConstraints(ConstraintSet.Resolve(Profile, FtmoRules));

        rm.UpdateEquityLevels(94_900m);
        var freshState = rm.CurrentState;

        var snap = Snapshot(94_900m, dailyDd: freshState.DailyDrawdownUsed, maxDd: freshState.MaxDrawdownUsed);
        var v = rm.Validate(LongEurUsd(), snap, Profile, 1.1000m);

        v.Should().Contain(x => x.Code == "DAILY_DD_LIMIT",
            "equity dropped below FTMO daily floor — trading must be blocked");
    }

    [Fact]
    public void Validate_TypicalMarketOrder_DoesNotTriggerMaxExposure()
    {
        // EURUSD market order, 20-pip SL, $100k equity, standard profile (maxExposurePercent 0.05),
        // no open positions. Must NOT contain a MAX_EXPOSURE violation.
        var intent20PipSl = new TradeIntent(
            Symbol.Parse("EURUSD"), TradeDirection.Long, OrderType.Market, null,
            new Price(1.0980m), null, "strat-1", "standard", "Test signal", DateTime.UtcNow);

        var rm = MakeRm();
        var snap = Snapshot(100_000);
        var v = rm.Validate(intent20PipSl, snap, Profile, 1.1000m);

        v.Should().NotContain(x => x.Code == "MAX_EXPOSURE");
    }

    [Fact]
    public void Circuit_WinFromNearLimit_AllowsContinuedTrading()
    {
        var registry = new SymbolInfoRegistry();
        registry.Register(EurUsd());
        var gov = Substitute.For<ITradingGovernor>();
        gov.Evaluate(Arg.Any<GovernorContext>())
            .Returns(new GovernorDecision(true, 1.0m, GovernorTradingState.Normal, "OK"));
        var rm = new RiskManager(registry, (_, _) => 1m,
            new NewsFilter(), new SessionFilter(), new StubClock(DateTime.UtcNow),
            Substitute.For<ICurrencyExposureTracker>(), gov, new SizingPolicyOptions());
        rm.InitializeDrawdownIfNeeded(100_000m);
        rm.SetActiveRuleSet(FtmoRules);
        rm.SetConstraints(ConstraintSet.Resolve(Profile, FtmoRules));

        rm.UpdateEquityLevels(95_500m);
        var state = rm.CurrentState;
        var snap = Snapshot(95_500m, dailyDd: state.DailyDrawdownUsed, maxDd: state.MaxDrawdownUsed);
        var v = rm.Validate(LongEurUsd(), snap, Profile, 1.1000m);
        v.Should().NotContain(x => x.Code == "DAILY_DD_LIMIT");
    }
}

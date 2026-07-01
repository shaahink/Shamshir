using TradingEngine.Engine;
using TradingEngine.Host;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// K2 gate (iter-36 cutover): the venue-feedback bridge (<see cref="KernelFeedback"/>) turns venue
/// executions + account snapshots into kernel events, so positions / drawdown / protection / breach are
/// evolved by the reducer (EngineState authority) — not by PositionTracker / AccountProcessor.
/// </summary>
[Trait("Category", "KernelAcceptance")]
[Trait("Speed", "Fast")]
public sealed class KernelFeedbackTests
{
    private static readonly Symbol Eurusd = Symbol.Parse("EURUSD");

    private static readonly SymbolInfo EurusdInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    private static Kernel BuildKernel()
    {
        var profile = new RiskProfile(
            "standard", "Standard", 0.01, 0.05, 0.10, 100.0, 0.10, 0.5, 0.1, 5,
            false, "ftmo-standard", LotSizingMethod.PercentRisk, 0.1m, 0m, 0.25, 1.5, 3);
        var ruleSet = new PropFirmRuleSet(
            "ftmo-standard", "ftmo-standard", "Fixed", 0.05, 0.10, 0.10, 0,
            "BalancePlusFloating", "22:00:00", "UTC", false, "High", 0, 0,
            false, "21:00:00", "20:00:00", "NextTradingDay", false);
        var constraints = ConstraintSet.Resolve(profile, ruleSet);
        return new Kernel(new KernelConfig(
            constraints, profile, new SizingPolicyOptions { FlattenAtFraction = 0.9 },
            ResolveSymbol: _ => EurusdInfo, ProjectOpenPositions: _ => [], Seed: 42));
    }

    private static EngineState FlatState() => new(
        new Dictionary<Guid, PositionState>(),
        new GovernorState(GovernorTradingState.Normal, 0, 0, 0, 1.0m, false, "Initial"),
        DrawdownReducer.CreateInitial(10_000m, "Fixed"),
        0, ProtectionState.None, AccountView.Flat);

    [Fact]
    public void FlatBook_EquityObserved_IsBalance_NotZero_AndDoesNotTripWatchdog()
    {
        var kernel = BuildKernel();
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // A flat book reports Equity == Balance (the C5 regression: it used to read 0 → false breach).
        var observed = KernelFeedback.FromAccount(new AccountUpdate(10_000m, 10_000m, 0m, t));
        observed.Balance.Should().Be(10_000m);
        observed.Equity.Should().Be(10_000m);

        var decision = kernel.Decide(FlatState(), observed);
        decision.State.Account.Equity.Should().Be(10_000m, "equity must be truthful, not zeroed");
        decision.State.Protection.InProtectionMode.Should().BeFalse("a flat, healthy book must not trip the breach watchdog");
        decision.Effects.OfType<CloseOpenPosition>().Should().BeEmpty("no force-close on a healthy book");
    }

    [Fact]
    public void SixPercentEquityDrop_EntersProtection_ExactlyOnce()
    {
        var kernel = BuildKernel();
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var state = FlatState();

        // 6% equity drop ⇒ daily DD 6% ≥ MaxDailyLoss(5%)×flatten(0.9)=4.5% ⇒ breach.
        var drop = kernel.Decide(state, KernelFeedback.FromAccount(new AccountUpdate(10_000m, 9_400m, -600m, t)));
        drop.State.Protection.InProtectionMode.Should().BeTrue("a 6% drop breaches the daily floor and enters protection");
        drop.State.Protection.Cause.Should().Be(ProtectionCause.DailyDrawdown);

        // A further breach-level observation while already protected must NOT re-enter / re-emit — the
        // kernel early-outs on InProtectionMode, so protection is entered exactly once.
        var again = kernel.Decide(drop.State, KernelFeedback.FromAccount(new AccountUpdate(10_000m, 9_300m, -700m, t.AddHours(1))));
        again.State.Protection.InProtectionMode.Should().BeTrue();
        again.Effects.Should().BeEmpty("already in protection — no new protection/force-close effects");
    }

    [Fact]
    public void SlCloseFill_ClosesOpenPosition_ViaReducer()
    {
        var kernel = BuildKernel();
        var t = new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var orderId = new Guid("22222222-2222-2222-2222-222222222222");

        // An Open position with the SL exit reason already recorded (as the BarClosed handler does when it
        // detects the stop) — the venue close fill arrives as an OrderFilled on an Open position.
        var open = new PositionState(
            orderId, orderId, Eurusd, TradeDirection.Long, 0.20m,
            new Price(1.0970m), new Price(1.0920m), new Price(1.1100m),
            t.AddHours(-2), "always-signal", PositionPhase.Open, FilledLots: 0.20m, CloseReason: "SL");
        var state = FlatState() with
        {
            Positions = new Dictionary<Guid, PositionState> { [orderId] = open },
            OpenPositionCount = 1,
        };

        // The venue reports the close fill at the SL price; the bridge maps it to OrderFilled.
        var closeFill = new ExecutionEvent(orderId, OrderState.Filled, new Price(1.0920m), 0.20m, null, t)
        {
            GrossProfit = -100m, NetProfit = -100m, Commission = 0m, Swap = 0m,
        };
        var evt = KernelFeedback.FromExecution(closeFill, Eurusd);
        evt.Should().BeOfType<OrderFilled>();

        var decision = kernel.Decide(state, evt!);

        decision.State.Positions.Should().BeEmpty("the SL fill closes the position via the reducer");
        var closed = decision.Effects.OfType<PublishTradeClosed>().Should().ContainSingle().Subject;
        closed.ExitReason.Should().Be("SL", "the kernel records the reason the BarClosed handler set, not FORCE");
        closed.ExitPrice.Value.Should().Be(1.0920m, "the close fills at the SL price carried on the venue event");
        closed.Lots.Should().Be(0.20m);
        decision.Effects.OfType<DeregisterRisk>().Should().ContainSingle();
    }
}

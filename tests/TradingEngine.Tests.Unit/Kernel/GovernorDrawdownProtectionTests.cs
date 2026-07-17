using TradingEngine.Engine;
using KernelCore = TradingEngine.Engine.Kernel;

namespace TradingEngine.Tests.Unit.Kernel;

/// <summary>
/// iter-37 Phase G — pure "break the rules" tests for the protective machinery, all through the kernel's
/// authoritative components (<see cref="GovernorMachine"/>, <see cref="DrawdownReducer"/>,
/// <see cref="PreTradeGate"/>, <see cref="Kernel"/>). No IHost, no IO. Each test names the OPEN-ISSUE it
/// guards so a future regression is traceable.
/// </summary>
internal static class GFx
{
    public static readonly Symbol Eurusd = Symbol.Parse("EURUSD");

    public static readonly SymbolInfo SymInfo = new(
        Eurusd, SymbolCategory.Forex, "EUR", "USD",
        0.0001m, 0.00001m, 100_000m, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m);

    public static readonly RiskProfile Profile = new(
        "standard", "Standard",
        RiskPerTradePercent: 0.01, MaxDailyDrawdownPercent: 0.05, MaxTotalDrawdownPercent: 0.10,
        MaxSlPips: 100.0, MaxExposurePercent: 1.0, DrawdownScaleThreshold: 0.5, DrawdownScaleFloor: 0.1,
        MaxConcurrentPositions: 5, AllowHedging: false, PropFirmRuleSetId: "ftmo");

    public static readonly SizingPolicyOptions Sizing = new();

    public static ConstraintSet Constraints(
        decimal maxDaily = 0.05m, decimal maxTotal = 0.10m,
        decimal maxWeekly = 0.05m, decimal maxMonthly = 0.10m,
        DailyDdBase dailyBase = DailyDdBase.InitialBalance,
        bool weeklyEnabled = false, bool monthlyEnabled = false,
        bool governorEnabled = true, bool forceClose = true) => new(
        "t", maxDaily, maxTotal, maxWeekly, maxMonthly, ProfitTarget: 0.10m,
        DrawdownType: "Fixed", DailyDdBase: dailyBase,
        RiskPerTrade: 0.01m, MaxConcurrentPositions: 5, MaxExposure: 1.0m,
        AllowTradesDuringNews: true, AllowWeekendHolding: true, ForceCloseOnBreach: forceClose,
        WeeklyDdEnabled: weeklyEnabled, MonthlyDdEnabled: monthlyEnabled, GovernorEnabled: governorEnabled);

    public static EngineState State(decimal balance = 10_000m, DrawdownState? dd = null) =>
        EngineState.Empty with
        {
            Drawdown = dd ?? DrawdownReducer.CreateInitial(balance, "Fixed"),
            Account = new AccountView(balance, balance, 0m),
        };

    public static OrderProposed Proposal(decimal slPips = 20m, decimal pipValue = 10m) => new(
        Guid.NewGuid(), Eurusd, TradeDirection.Long, OrderType.Market,
        LimitPrice: null, StopLoss: new Price(1.0980m), TakeProfit: null,
        StrategyId: "s", SignalPriceMid: 1.1000m, SlPips: slPips, PipValuePerLot: pipValue,
        OccurredAtUtc: new DateTime(2026, 1, 7, 12, 0, 0, DateTimeKind.Utc));
}

[Trait("Category", "Kernel")]
[Trait("Speed", "Fast")]
public sealed class GovernorTests
{
    private const int StreakPauseAt = 3, CoolingOffBars = 5;
    private static readonly double[] NoBands = [];

    private static GovernorState Evaluate(GovernorState s, decimal dailyDdFraction = 0m, decimal dayNetPnLFraction = 0m,
        bool profitLockEnabled = false) =>
        GovernorMachine.EvaluateStatic(s, dailyDdFraction, dayNetPnLFraction,
            StreakPauseAt, CoolingOffBars, NoBands, NoBands,
            profitLockEnabled, profitLockFraction: 0.5, maxDailyLoss: 0.05m, streakReduceAt: 99, streakMultiplier: 1.0);

    [Fact] // Guards H8/BUG-09 (loss-streak → pause)
    public void Governor_LossStreak_EntersCoolingOff()
    {
        var s = GovernorMachine.CreateInitial();
        for (var i = 0; i < StreakPauseAt; i++)
            s = GovernorMachine.ApplyTradeClosed(s, isWin: false, isLoss: true);
        s.ConsecutiveLosses.Should().Be(StreakPauseAt);

        s = Evaluate(s);

        s.State.Should().Be(GovernorTradingState.CoolingOff, "N consecutive losses must pause trading");
        s.CoolingOffBarsRemaining.Should().Be(CoolingOffBars);
    }

    [Fact] // Guards BUG-09 (cooling-off counter decrements per bar and expires)
    public void Governor_CoolingOff_DecrementsPerBar_AndExpires()
    {
        var s = GovernorMachine.CreateInitial() with
        {
            State = GovernorTradingState.CoolingOff, CoolingOffBarsRemaining = 2,
        };

        s = GovernorMachine.ApplyBar(s);
        s.CoolingOffBarsRemaining.Should().Be(1, "each bar decrements the cooling-off window");
        s.State.Should().Be(GovernorTradingState.CoolingOff);

        s = GovernorMachine.ApplyBar(s);
        s.State.Should().Be(GovernorTradingState.Normal, "trading resumes when the window expires");
        s.CoolingOffBarsRemaining.Should().Be(0);
    }

    [Fact] // Guards H7 (profit-lock blocks new risk; cleared only by the daily reset)
    public void Governor_ProfitLock_BlocksNewRisk_UntilDailyReset()
    {
        var locked = GFx.State() with
        {
            Governor = GovernorMachine.CreateInitial() with
            {
                State = GovernorTradingState.ProfitLocked, ProfitLockedToday = true,
            },
        };
        var c = GFx.Constraints();

        var blocked = PreTradeGate.Evaluate(locked, GFx.Proposal(), c, GFx.Profile, GFx.Sizing, GFx.SymInfo, []);
        blocked.Accepted.Should().BeFalse();
        blocked.RejectReason.Should().StartWith("GOVERNOR", "a profit-locked governor blocks new entries");

        // H7: the daily reset clears the lock → the gate no longer rejects for the governor.
        var afterReset = locked with { Governor = GovernorMachine.ApplyDailyReset(locked.Governor) };
        var resumed = PreTradeGate.Evaluate(afterReset, GFx.Proposal(), c, GFx.Profile, GFx.Sizing, GFx.SymInfo, []);
        resumed.Accepted.Should().BeTrue("after the daily reset clears the profit lock, trading resumes");
    }

    [Fact] // Guards H7 (daily reset clears profit lock + returns to Normal)
    public void Governor_DailyReset_ClearsProfitLockAndResumesTrading()
    {
        var s = GovernorMachine.CreateInitial() with
        {
            State = GovernorTradingState.ProfitLocked, ProfitLockedToday = true,
        };

        s = GovernorMachine.ApplyDailyReset(s);

        s.State.Should().Be(GovernorTradingState.Normal);
        s.ProfitLockedToday.Should().BeFalse("the daily reset clears the per-day profit lock");
    }
}

[Trait("Category", "Kernel")]
[Trait("Speed", "Fast")]
public sealed class DrawdownGateTests
{
    [Fact] // Guards C3/H4 (trailing max-DD floor tracks PEAK, not current equity)
    public void Drawdown_Trailing_FloorTracksPeakNotCurrent()
    {
        // Push the peak up to 11,000 then drop equity to 10,200. The trailing floor must stay anchored to
        // the peak (11,000 * 0.9 = 9,900), NOT slide down with current equity (which would be 10,200 * 0.9).
        var dd = DrawdownReducer.CreateInitial(10_000m, "Trailing");
        dd = DrawdownReducer.Apply(dd, 11_000m); // peak rises
        dd = DrawdownReducer.Apply(dd, 10_200m); // equity falls; peak holds

        dd.PeakEquity.Should().Be(11_000m);
        dd.GetMaxDrawdownFloor(0.10m).Should().Be(9_900m, "trailing floor = PeakEquity * (1 - maxTotalLoss)");
    }

    [Fact] // Guards H1 (fixed max-DD floor uses INITIAL balance, not the grown balance)
    public void Drawdown_Fixed_FloorUsesInitialNotGrownBalance()
    {
        var dd = DrawdownReducer.CreateInitial(10_000m, "Fixed") with { PeakEquity = 11_000m };
        dd.GetMaxDrawdownFloor(0.10m).Should().Be(9_000m, "fixed floor anchors to InitialAccountBalance even after growth");
    }

    [Fact] // Guards H3 + F79: DailyDdBase selects the ALLOWANCE BASE only; the anchor is always day start.
    public void Drawdown_DailyBase_SelectsAllowanceBase_NotAnchor()
    {
        // Day opened at 9,000 (down from a 10,000 initial), equity dips to 8,700 intraday.
        //   • DailyStart base:     (9,000 − 8,700) / 9,000  = 3.33% of the day-start allowance.
        //   • InitialBalance base: (9,000 − 8,700) / 10,000 = 3.00% of the initial-capital allowance.
        // F79 regression: the old InitialBalance arm anchored at initial too — flat at day start it
        // reported (10,000 − 9,000)/10,000 = 10% "daily" DD, i.e. cumulative DD relabeled daily,
        // which re-breached the daily limit every day forever once an account sat below initial.
        var dayStart = DrawdownReducer.CreateInitial(10_000m, "Fixed") with { DailyStartEquity = 9_000m };

        var flatDaily = DrawdownReducer.Apply(dayStart with { DailyDdBaseMode = "DailyStart" }, 9_000m);
        var flatInitial = DrawdownReducer.Apply(dayStart with { DailyDdBaseMode = "InitialBalance" }, 9_000m);
        flatDaily.CurrentDailyDrawdown.Should().Be(0m);
        flatInitial.CurrentDailyDrawdown.Should().Be(0m, "a flat day has zero DAILY drawdown regardless of base (F79)");

        var dipDaily = DrawdownReducer.Apply(dayStart with { DailyDdBaseMode = "DailyStart" }, 8_700m);
        var dipInitial = DrawdownReducer.Apply(dayStart with { DailyDdBaseMode = "InitialBalance" }, 8_700m);
        dipDaily.CurrentDailyDrawdown.Should().BeApproximately(0.0333m, 0.0002m);
        dipInitial.CurrentDailyDrawdown.Should().Be(0.03m);
    }

    [Fact] // F79 gate leg: the worst-case daily floor hangs off day start, not statically off initial.
    public void Drawdown_DailyFloor_IsDayAnchored_InInitialBalanceMode()
    {
        // Same scenario the old test pinned the BUG with: day start 9,000 on a 10,000 initial,
        // proposal worst-case ≈ 8,910. Old floor = 10,000 * 0.95 = 9,500 (static) → rejected forever
        // below initial. F79 floor = 9,000 − 0.05 × 10,000 = 8,500 → the day's true FTMO budget passes.
        var dd = DrawdownReducer.CreateInitial(10_000m, "Fixed") with { DailyStartEquity = 9_000m };
        var state = GFx.State(dd: dd) with { Account = new AccountView(9_000m, 9_000m, 0m) };

        var initialBase = PreTradeGate.Evaluate(state, GFx.Proposal(),
            GFx.Constraints(maxTotal: 0.20m, dailyBase: DailyDdBase.InitialBalance),
            GFx.Profile, GFx.Sizing, GFx.SymInfo, []);

        initialBase.Accepted.Should().BeTrue(
            "the daily floor is day-start − 5% × initial (verified FTMO semantics), not a static 95% of initial (F79)");
    }

    [Fact] // Guards H2 (weekly + monthly DD enforced in the production gate)
    public void Drawdown_Weekly_Monthly_Enforced()
    {
        var weeklyBreached = GFx.State(dd: DrawdownReducer.CreateInitial(10_000m, "Fixed") with { CurrentWeeklyDrawdown = 0.05m });
        var monthlyBreached = GFx.State(dd: DrawdownReducer.CreateInitial(10_000m, "Fixed") with { CurrentMonthlyDrawdown = 0.10m });

        PreTradeGate.Evaluate(weeklyBreached, GFx.Proposal(),
            GFx.Constraints(weeklyEnabled: true), GFx.Profile, GFx.Sizing, GFx.SymInfo, [])
            .RejectReason.Should().Be("WEEKLY_DD_LIMIT");

        PreTradeGate.Evaluate(monthlyBreached, GFx.Proposal(),
            GFx.Constraints(monthlyEnabled: true), GFx.Profile, GFx.Sizing, GFx.SymInfo, [])
            .RejectReason.Should().Be("MONTHLY_DD_LIMIT");
    }
}

[Trait("Category", "Kernel")]
[Trait("Speed", "Fast")]
public sealed class ProtectionWatchdogTests
{
    private static readonly DateTime T = new(2026, 1, 7, 22, 0, 0, DateTimeKind.Utc);

    private static KernelCore BuildKernel(ConstraintSet? c = null) => new(new KernelConfig(
        c ?? GFx.Constraints(), GFx.Profile, GFx.Sizing,
        ResolveSymbol: _ => GFx.SymInfo, ProjectOpenPositions: _ => [], Seed: 42));

    [Fact] // Guards C5 (a flat book, Equity == Balance, must not trip the breach watchdog)
    public void Protection_FlatBook_NoFalseBreach()
    {
        var dd = DrawdownReducer.CreateInitial(10_000m, "Fixed"); // all drawdown == 0
        var (cause, _) = KernelCore.EvaluateDrawdownBreach(dd, GFx.Constraints(), 0.9m);
        cause.Should().Be(ProtectionCause.None, "a flat book is not a breach");
    }

    [Fact] // Guards K2 idempotency (enter protection exactly once on a daily-DD breach)
    public void Protection_EntersOnce_OnDailyDdBreach()
    {
        var kernel = BuildKernel();
        var s0 = GFx.State();

        // 6% equity drop → daily DD 0.06 ≥ 0.045 (0.05 * 0.9 flatten) → enter protection.
        var d1 = kernel.Decide(s0, new EquityObserved(10_000m, 9_400m, 0m, T));
        d1.State.Protection.InProtectionMode.Should().BeTrue();
        d1.State.Protection.Cause.Should().Be(ProtectionCause.DailyDrawdown);

        // A second, deeper observation while already protected is idempotent — no re-entry, no new cause.
        var d2 = kernel.Decide(d1.State, new EquityObserved(10_000m, 9_000m, 0m, T.AddHours(1)));
        d2.State.Protection.InProtectionMode.Should().BeTrue();
        d2.State.Protection.Cause.Should().Be(ProtectionCause.DailyDrawdown);
        d2.Effects.Should().BeEmpty("already-protected equity observations short-circuit (no repeat force-close)");
    }

    [Fact] // Guards C4 (MaxDD protection auto-exits on the daily reset per ResetPolicy)
    public void Protection_MaxDd_AutoExitsOnReset()
    {
        var nextDay = GFx.State() with
        {
            Protection = new ProtectionState(true, ProtectionCause.MaxDrawdown, "max", "NextTradingDay", null),
        };
        BuildKernel().Decide(nextDay, new DayRolled(T)).State.Protection.InProtectionMode
            .Should().BeFalse("MaxDD protection with NextTradingDay policy clears on the day roll (C4)");

        var never = GFx.State() with
        {
            Protection = new ProtectionState(true, ProtectionCause.MaxDrawdown, "max", "Never", null),
        };
        BuildKernel().Decide(never, new DayRolled(T)).State.Protection.InProtectionMode
            .Should().BeTrue("a Never reset policy keeps MaxDD protection across the day roll");
    }

    [Fact] // Guards C4 sibling (daily-DD protection clears on the day roll)
    public void Protection_DailyDd_AutoExitsOnReset()
    {
        var s = GFx.State() with
        {
            Protection = new ProtectionState(true, ProtectionCause.DailyDrawdown, "daily", "NextTradingDay", null),
        };
        BuildKernel().Decide(s, new DayRolled(T)).State.Protection.InProtectionMode
            .Should().BeFalse("daily-DD protection always clears on the next day");
    }
}

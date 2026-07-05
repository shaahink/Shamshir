namespace TradingEngine.Tests.Unit.Phase33Tests;

using TradingEngine.Services.ExitLab;

/// <summary>
/// P3.3 exit replayer unit tests — core algorithm correctness. Each test hand-computes
/// pip values from fixed fixture data so failures are arithmetic, not regression.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ExitReplayerTests
{
    private const double ReferenceAtrPips = 20.0;

    private static TradeExcursionInput MakeLong(decimal entry, Price sl, decimal pipSize, params (int t, double hi, double lo)[] points) => new()
    {
        Direction = TradeDirection.Long,
        EntryPrice = entry,
        InitialStopLoss = sl,
        PipSize = pipSize,
        SpreadPips = 2.0,
        Path = points.Select(p => new ExcursionPoint(p.t, p.hi, p.lo)).ToList(),
    };

    private static TradeExcursionInput MakeShort(decimal entry, Price sl, decimal pipSize, params (int t, double hi, double lo)[] points) => new()
    {
        Direction = TradeDirection.Short,
        EntryPrice = entry,
        InitialStopLoss = sl,
        PipSize = pipSize,
        SpreadPips = 2.0,
        Path = points.Select(p => new ExcursionPoint(p.t, p.hi, p.lo)).ToList(),
    };

    private static TradeExcursionInput LongTpTrade() => MakeLong(1.1000m, new Price(1.0970m), 0.0001m,
        (0, 5.0, -10.0),
        (60, 15.0, -25.0),
        (120, 65.0, -25.0)); // TP: 65 >= 60, SL not hit (lo=-25 > -30)

    [Fact]
    public void Long_TP_Hit_ReturnsCorrectRMultiple()
    {
        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = 2.0,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(LongTpTrade(), rule);

        outcome.Kind.Should().Be(ExitKind.TP);
        outcome.BarsHeld.Should().Be(3);
        outcome.RMultiple.Should().BeApproximately(2.0, 0.01);
    }

    [Fact]
    public void Long_SL_Hit_ReturnsNegativeR()
    {
        var trade = MakeLong(1.1000m, new Price(1.0970m), 0.0001m,
            (0, 5.0, -10.0),
            (60, 8.0, -25.0),
            (120, 3.0, -32.0)); // SL hit: lo=-32 <= -30

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.SL);
        outcome.RMultiple.Should().BeApproximately(-1.0, 0.01);
    }

    [Fact]
    public void SL_First_When_Both_Hit_Same_Bar()
    {
        // Bar where both SL and TP trip — SL wins (conservative).
        var trade = MakeLong(1.1000m, new Price(1.0970m), 0.0001m,
            (0, 65.0, -32.0)); // TP+65 >= 60 AND SL lo=-32 <= -30

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = 2.0,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.SL);
        outcome.RMultiple.Should().BeApproximately(-1.0, 0.01);
    }

    [Fact]
    public void Short_SL_Hit_ReturnsNegativeR()
    {
        // Short SL at 1.5 × 20 = 30 pips above entry → hiPips = +30 is SL breach
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0, 5.0, -2.0),
            (60, 32.0, 15.0)); // hi=+32 >= +30 → SL hit

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.SL);
        outcome.RMultiple.Should().BeApproximately(-1.0, 0.01);
    }

    [Fact]
    public void Short_TP_Hit_ReturnsPositiveR()
    {
        // Short TP at 2.0RR: 30 risk × 2 = 60 pips below entry
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0, 5.0, -5.0),
            (60, 10.0, -40.0),
            (120, 28.0, -65.0)); // bar lo=-65 <= tp=-60, hi=+28 < sl=+30 (SL not hit on this bar)

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = 2.0,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.TP);
        outcome.RMultiple.Should().BeApproximately(2.0, 0.01);
    }

    [Fact]
    public void BE_Triggers_Then_SL_At_BE_Level()
    {
        var trade = MakeLong(1.1000m, new Price(1.0970m), 0.0001m,
            (0, 5.0, -5.0),
            (60, 35.0, 10.0),   // hi=+35 >= +30 (beTriggerR × risk), BE arms at entry + offset
            (120, 40.0, 0.0));  // bar low hits entry (BE level)

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            BeTriggerR = 1.0,
            BeOffsetPips = 0,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.Breakeven);
        outcome.RMultiple.Should().BeApproximately(0.0, 0.01);
    }

    [Fact]
    public void Trail_Follows_Then_Stops_Out()
    {
        // Trail at 1.0 × ATR = 20 pips behind the best favorable extreme.
        var trade = MakeLong(1.1000m, new Price(1.0970m), 0.0001m,
            (0, 25.0, 5.0),     // trail = 25 - 20 = 5
            (60, 50.0, 20.0),   // trail = 50 - 20 = 30
            (120, 55.0, 28.0),  // no hit
            (180, 40.0, 26.0)); // trail stop at 30, bar low 26 < 30 → trail hit

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            TrailAtrMultiple = 1.0,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.TrailingStop);
        outcome.RMultiple.Should().BeApproximately(1.0, 0.01); // trail closed at 30 pips = 1R
    }

    [Fact]
    public void EndOfData_Returns_Last_Bar_Close()
    {
        var trade = MakeLong(1.1000m, new Price(1.0970m), 0.0001m,
            (0, 5.0, -10.0),
            (60, 8.0, -5.0));

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.EndOfData);
        outcome.BarsHeld.Should().Be(2);
        outcome.RPips.Should().BeApproximately(-5.0, 0.01);
        outcome.RMultiple.Should().BeApproximately(-5.0 / 30.0, 0.01);
    }

    [Fact]
    public void Empty_Path_Returns_EndOfData_With_ZeroR()
    {
        var trade = MakeLong(1.1000m, new Price(1.0970m), 0.0001m);

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.EndOfData);
        outcome.BarsHeld.Should().Be(0);
        outcome.RMultiple.Should().Be(0);
    }

    // ── P3.3 critical fix: Short-direction bugs (A1–A6) ──

    [Fact]
    public void A1_Short_Trail_Tightens_Not_Widens()
    {
        // Short: entry 1.1000, SL 1.1030 (slPips=+30, risk=30).
        // Trail at 1.0×ATR=20 pips behind the best signed low.
        // Bar 0: lo=-50 → trail=-50+20=-30. Math.Min(+30,-30)=-30. SL→-30.
        // Bar 1: hi=-25 >= -30 → trail hit at -30 pips = 1R profit.
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0,  -5.0, -50.0),    // lo=-50: trail moves SL to -30
            (60, -25.0, -45.0));   // hi=-25 >= -30 → trail hit

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            TrailAtrMultiple = 1.0,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.TrailingStop);
        outcome.BarsHeld.Should().Be(2);
        outcome.RPips.Should().BeApproximately(-30.0, 0.01);
        outcome.RMultiple.Should().BeApproximately(1.0, 0.01); // 30 profit / 30 risk
    }

    [Fact]
    public void A2_Short_BE_Moves_SL_Below_Entry()
    {
        // Short BE: trigger at 1.0R = -30 pips (signed). Offset 2 pips.
        // Bar 1 step 2: lo=-35 <= -30 → BE arms. SL → -2 (offset below entry).
        // Bar 2 step 1: hi=+5 >= -2 → BE hit. RPips=-2, RMultiple=+2/30≈+0.067.
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0,  3.0, -10.0),     // lo=-10: BE not yet (-10 > -30)
            (60, 5.0, -35.0),     // step 2: lo=-35 <= -30 → BE arms, SL→-2
            (120, 5.0, -15.0));    // step 1: hi=+5 >= -2 → BE hit

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            BeTriggerR = 1.0,
            BeOffsetPips = 2,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.Breakeven);
        outcome.BarsHeld.Should().Be(3);
        outcome.RPips.Should().BeApproximately(-2.0, 0.01);
        outcome.RMultiple.Should().BeApproximately(2.0 / 30.0, 0.01); // tiny profit from BE offset
    }

    [Fact]
    public void A3_Short_Partial_Moves_Remaining_Sl_To_Be()
    {
        // Short: partial trigger 0.5R=-15 pips. Offset 2 pips.
        // Bar 0 step 2: lo=-20 <= -15 → partial fires. SL→-2. Also set beArmed=true.
        // Bar 1 step 1: hi=+5 >= -2 → hit. Kind=Breakeven. RPips=-2, RMultiple≈+0.067.
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0,  5.0, -20.0),     // lo=-20 <= -15 → partial fires, SL→-2
            (60, 5.0, -10.0));     // hi=+5 >= -2 → hit

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            PartialTriggerR = 0.5,
            PartialCloseFraction = 0.5,
            BeOffsetPips = 2,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.Breakeven);
        outcome.BarsHeld.Should().Be(2);
        outcome.RPips.Should().BeApproximately(-2.0, 0.01);
        outcome.RMultiple.Should().BeApproximately(2.0 / 30.0, 0.01);
    }

    [Fact]
    public void A4_Short_EndOfData_Returns_Adverse_HiPips()
    {
        // Short: last bar hi=+15 (adverse move). No exits triggered.
        // closePips should be +15 (loss), NOT -15 (buggy inverted sign).
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0,  5.0, -10.0),
            (60, 15.0, -5.0));   // hi=+15, lo=-5 — no SL/TP hit

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.EndOfData);
        outcome.RPips.Should().BeApproximately(15.0, 0.01);  // +15 = adverse for short
        outcome.RMultiple.Should().BeApproximately(-0.5, 0.01); // 15/30 = -0.5R
    }

    [Fact]
    public void A5_Short_Partial_Requires_Signed_Threshold()
    {
        // Short: partial trigger 0.5R=-15 pips. Bar 0 lo=-5 → too small (5 pips profit).
        // Bar 1 lo=-20 <= -15 → partial fires. SL→-2, beArmed=true.
        // Bar 1 step 1 (before partial): hi=3 < +30 → no SL hit yet.
        // Bar 1 step 2: partial fires, SL→-2, beArmed=true.
        // Bar 2 step 1: hi=0 >= -2 → hit. Kind=Breakeven.
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0,  3.0, -5.0),      // lo=-5 → NOT enough for partial (-5 > -15)
            (60, 3.0, -20.0),     // step 2: lo=-20 <= -15 → partial fires, SL→-2
            (120, 0.0, -25.0));    // step 1: hi=0 >= -2 → hit

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            PartialTriggerR = 0.5,
            PartialCloseFraction = 0.5,
            BeOffsetPips = 2,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.Breakeven);
        outcome.BarsHeld.Should().Be(3);
    }

    [Fact]
    public void A6_Short_BE_Arms_On_Signed_Threshold()
    {
        // Short: BE trigger 1.0R=-30 pips. Offset 0.
        // Bar 0: lo=-5 → too small for BE (-5 > -30).
        // Bar 1 step 2: lo=-35 <= -30 → BE arms. SL→0.
        // Bar 2 step 1: hi=0 >= 0 → BE hit. Kind=Breakeven. 0R.
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0,  5.0, -5.0),      // lo=-5: BE not yet
            (60, 8.0, -35.0),     // lo=-35 <= -30 → BE arms, SL→0
            (120, 0.0, -15.0));    // hi=0 >= 0 → BE hit

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            BeTriggerR = 1.0,
            BeOffsetPips = 0,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.Breakeven);
        outcome.BarsHeld.Should().Be(3);
        outcome.RMultiple.Should().BeApproximately(0.0, 0.01);
    }

    [Fact]
    public void A6b_Short_BE_Does_Not_Fire_On_Shallow_Favorable_Move()
    {
        // Short: BE trigger 1.0R=-30 pips. Both bars only drop -5 and -8.
        // With BUGGY magnitude comparison: bar 0 favorableExtreme=+5 >= -30 → fires on bar 0.
        // FIXED: lo=-5 > -30 and lo=-8 > -30 → BE never arms. EndOfData.
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0,  5.0, -5.0),      // lo=-5 → too small
            (60, 8.0, -8.0));     // lo=-8 → too small

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            BeTriggerR = 1.0,
            BeOffsetPips = 0,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.EndOfData);
    }
}

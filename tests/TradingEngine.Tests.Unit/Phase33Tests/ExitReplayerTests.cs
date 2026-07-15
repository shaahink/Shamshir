namespace TradingEngine.Tests.Unit.Phase33Tests;

using TradingEngine.Services.ExitLab;

/// <summary>
/// P3.3 / P4.5.3 exit replayer unit tests — core algorithm correctness including the 4 fix areas:
/// (a) short-side spread, (b) BE/trail cadence, (c) MAE, (d) partial-TP rejection.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ExitReplayerTests
{
    private const double ReferenceAtrPips = 20.0;
    private const double Spread = 2.0;

    private static TradeExcursionInput MakeLong(decimal entry, Price sl, decimal pipSize, params (int t, double hi, double lo)[] points) => new()
    {
        Direction = TradeDirection.Long,
        EntryPrice = entry,
        InitialStopLoss = sl,
        PipSize = pipSize,
        SpreadPips = Spread,
        Path = points.Select(p => new ExcursionPoint(p.t, p.hi, p.lo)).ToList(),
    };

    private static TradeExcursionInput MakeShort(decimal entry, Price sl, decimal pipSize, params (int t, double hi, double lo)[] points) => new()
    {
        Direction = TradeDirection.Short,
        EntryPrice = entry,
        InitialStopLoss = sl,
        PipSize = pipSize,
        SpreadPips = Spread,
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

    // ── P4.5.3a: Short-side spread ──
    // The venue detects short SL/TP on the ASK (bar shifted by full spread) and fills short exits
    // at ask. The recorded path is bid-relative, so the replayer must add SpreadPips for detection
    // and fill. Every short exit (SL/TP/Trail/BE/EndOfData) gets +spreadPips on the exit price.

    [Fact]
    public void Short_SL_Hit_ReturnsNegativeR_WithSpread()
    {
        // Short entry 1.1000 (bid), SL at 1.1030 → slPips = +30.
        // Bar 1 (t=60): hi=+32. With spread: barHiPips=32+2=34 >= 30 → SL hit.
        // Exit pips = slPips + spread = 30 + 2 = 32. R = -32/30 = -1.067.
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0, 5.0, -2.0),
            (60, 32.0, 15.0));

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.SL);
        outcome.RPips.Should().BeApproximately(32.0, 0.01);
        outcome.RMultiple.Should().BeApproximately(-32.0 / 30.0, 0.01);
    }

    [Fact]
    public void Short_TP_Hit_ReturnsPositiveR_WithSpread()
    {
        // Short: TP at -60 pips. Bar lo=-65 + spread(2) = -63 <= -60 → TP hit.
        // Bar hi=25 + spread(2) = 27 < 30 → SL NOT hit. Exit pips = -60 + 2 = -58. R = -(-58)/30 = 1.933.
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0, 5.0, -5.0),
            (60, 10.0, -40.0),
            (120, 25.0, -65.0));

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = 2.0,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.TP);
        outcome.RPips.Should().BeApproximately(-58.0, 0.01);
        outcome.RMultiple.Should().BeApproximately(1.933, 0.01);
    }

    [Fact]
    public void Short_EndOfData_ReturnsAdverseHiPips_WithSpread()
    {
        // Short: last bar hi=+15. With spread: close at 15+2=17 (ask). R = -17/30 = -0.567.
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0, 5.0, -10.0),
            (60, 15.0, -5.0));

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.EndOfData);
        outcome.RPips.Should().BeApproximately(17.0, 0.01);
        outcome.RMultiple.Should().BeApproximately(-17.0 / 30.0, 0.01);
    }

    // ── BE / trailing cadence (P4.5.3b) ──
    // BE/trail updates happen at decision-bar BOUNDARIES using the accumulated extreme
    // from the entire decision bar, not per-path-point. The fixture uses exactly 60-min
    // interval points (= one point per H1 decision bar), so for single-point-per-bar
    // fixtures the cadence is transparent. Fixtures that need BE to arm on one bar and
    // exit on the next bar work naturally — that IS the venue's real ordering.

    [Fact]
    public void Long_BE_Arms_OnBullishBar_ExitOnNext()
    {
        // Bar 0 (t=0): hi=5 < beTrigger(30). No arming.
        // Bar 1 (t=60): hi=35 >= 30 → BE arms at bar boundary (t=60), SL→0.
        // Bar 2 (t=120): exit check first at SL=0. lo=0 <= 0 → Breakeven exit.
        // No spread on long side.
        var trade = MakeLong(1.1000m, new Price(1.0970m), 0.0001m,
            (0, 5.0, -5.0),
            (60, 35.0, 10.0),
            (120, 40.0, 0.0));  // lo=0 hits BE

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
        outcome.RPips.Should().BeApproximately(0.0, 0.01);
        outcome.RMultiple.Should().BeApproximately(0.0, 0.01);
    }

    [Fact]
    public void Long_Trail_FollowsThenStopsOut()
    {
        var trade = MakeLong(1.1000m, new Price(1.0970m), 0.0001m,
            (0, 25.0, 5.0),
            (60, 50.0, 20.0),
            (120, 55.0, 28.0),
            (180, 40.0, 26.0));

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            TrailAtrMultiple = 1.0,
            ReferenceAtrPips = 20.0,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.TrailingStop);
        // With H1 cadence: bar 0 trail at t=60 → SL=5, bar 1 trail at t=120 → SL=30,
        // exit check at t=180 with lo=26 < 30 → hit at 30 pips.
        outcome.RPips.Should().BeApproximately(30.0, 0.01);
        outcome.RMultiple.Should().BeApproximately(1.0, 0.01);
    }

    // ── P4.5.3c: MAE correctness ──
    // MAE tracks the MAXIMUM adverse excursion (biggest distance in the wrong direction).
    // Favorable-only bars contribute zero adverse. The old < comparison was inverted.

    [Fact]
    public void Mae_TracksMaximumAdverse_CorrectSign()
    {
        // Long: bar 0 lo=-10 (adverse=10), bar 1 lo=-25 (adverse=25). MAE = 25 pipps.
        var trade = MakeLong(1.1000m, new Price(1.0970m), 0.0001m,
            (0, 5.0, -10.0),
            (60, 15.0, -25.0));

        var rule = new ExitRule
        {
            SlAtrMultiple = 5.0, // wide enough to avoid any hit
            TpRrMultiple = null,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.EndOfData);
        outcome.MaePips.Should().BeApproximately(25.0, 0.01);
        outcome.MfePips.Should().BeApproximately(15.0, 0.01);
    }

    [Fact]
    public void Mfe_TracksMaximumFavorable_CorrectSign()
    {
        // Long: bar 0 hi=8, bar 1 hi=35. MFE = 35 pipps.
        var trade = MakeLong(1.1000m, new Price(1.0970m), 0.0001m,
            (0, 8.0, -5.0),
            (60, 35.0, -10.0));

        var rule = new ExitRule
        {
            SlAtrMultiple = 5.0,
            TpRrMultiple = null,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.MfePips.Should().BeApproximately(35.0, 0.01);
        outcome.MaePips.Should().BeApproximately(10.0, 0.01);
    }

    [Fact]
    public void Short_Mae_CorrectSign()
    {
        // Short: bar 0 hi=5+2=7 (adverse=7), bar 1 hi=15+2=17 (adverse=17). MAE = 17.
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0, 5.0, -10.0),
            (60, 15.0, -5.0));

        var rule = new ExitRule
        {
            SlAtrMultiple = 5.0,
            TpRrMultiple = null,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.MaePips.Should().BeApproximately(17.0, 0.01);
    }

    // ── P4.5.3d: Partial-TP rejection ──

    [Fact]
    public void Replay_Throws_OnPartialTrigger()
    {
        var trade = MakeLong(1.1000m, new Price(1.0970m), 0.0001m, (0, 5.0, -5.0));
        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            PartialTriggerR = 0.5,
            PartialCloseFraction = 0.5,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var act = () => ExitReplayer.Replay(trade, rule);

        act.Should().Throw<NotSupportedException>();
    }

    // ── Short-side BE/trail with spread ──

    [Fact]
    public void Short_BE_Arms_ExitAtBeLevel_WithSpread()
    {
        // Short: BE trigger at -30 pips. bar 1 (t=60 boundary): bar 0's loPips=-50+2=-48.
        // BE: -48 <= -30 → BE arms at t=60 boundary, SL→0.
        // bar 1 (t=60): exit check after BE arm. hiPips=-35+2=-33 >= 0? No.
        // bar 2 (t=120): boundary from bar1: loPips=-10+2=-8. BE already armed.
        //   exit check: hiPips=2+2=4 >= 0 → BE hit.
        // Exit pips = 0 + spread = 2. For short, RPips=+2 (price above entry → loss).
        // R = 2/30 * (-1) = -0.067 (small loss from spread).
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0, 5.0, -50.0),      // bar 0: lo=-50 with spread=-48 <= -30 → BE arms at boundary
            (60, -35.0, -10.0),   // bar 1: no exit (hiPips=-35+2=-33 < 0)
            (120, 2.0, -20.0));    // bar 2: hiPips=2+2=4 >= 0 → BE hit

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
        outcome.RPips.Should().BeApproximately(2.0, 0.01);
        outcome.RMultiple.Should().BeApproximately(-0.067, 0.01);
    }

    [Fact]
    public void Short_Trail_Hits_WithSpread()
    {
        // Short: trail at 1.0×ATR=20. bar 0 lo=-50 → spread-adjusted loPips=-48.
        // At t=60 boundary: trail from -48: -48+20 = -28. SL→-28.
        // bar 1 hi=-25 → spread-adjusted hiPips=-23 >= -28 → trail hit.
        // Exit pips = -28 + 2 = -26. R = -(-26)/30 = 0.867.
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0, -5.0, -50.0),
            (60, -25.0, -45.0));

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            TrailAtrMultiple = 1.0,
            ReferenceAtrPips = ReferenceAtrPips,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.TrailingStop);
        outcome.RPips.Should().BeApproximately(-26.0, 0.01);
        outcome.RMultiple.Should().BeApproximately(0.867, 0.01);
    }

    // ── Remaining coverage (unchanged semantics, verified unchanged) ──

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

    [Fact]
    public void Short_BE_Does_Not_Fire_On_Shallow_Move()
    {
        // Short: BE trigger -30 pips. Both bars have lo=-5 and lo=-8 (too shallow).
        // BE never arms. EndOfData with spread on close.
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0, 5.0, -5.0),
            (60, 8.0, -8.0));

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

    [Fact]
    public void ValidationGate_FullTrailingAfterBE_RoundTrip()
    {
        var trade = MakeLong(1.1000m, new Price(1.0970m), 0.0001m,
            (0, 10.0, -5.0),
            (60, 35.0, -8.0),
            (120, 28.0, 12.0));

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            BeTriggerR = 1.0,
            BeOffsetPips = 0,
            TrailAtrMultiple = 1.0,
            ReferenceAtrPips = 20.0,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.Breakeven);
        outcome.BarsHeld.Should().Be(3);
        outcome.RPips.Should().BeApproximately(15.0, 0.01);
        outcome.RMultiple.Should().BeApproximately(0.5, 0.01);
        outcome.MaePips.Should().BeApproximately(8.0, 0.01);
        outcome.MfePips.Should().BeApproximately(35.0, 0.01);
    }

    [Fact]
    public void ValidationGate_ComplexPath_SLHitWithBEArmed()
    {
        var trade = MakeLong(1.1000m, new Price(1.0970m), 0.0001m,
            (0, 15.0, -20.0),
            (60, 40.0, 5.0),
            (120, 38.0, 2.0),
            (180, 30.0, -4.0));

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = null,
            BeTriggerR = 1.0,
            BeOffsetPips = 3.0,
            ReferenceAtrPips = 20.0,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.Breakeven);
        outcome.RPips.Should().BeApproximately(3.0, 0.01);
        outcome.RMultiple.Should().BeApproximately(0.1, 0.01);
        outcome.MaePips.Should().BeApproximately(20.0, 0.01);
        outcome.MfePips.Should().BeApproximately(40.0, 0.01);
    }

    [Fact]
    public void ValidationGate_ShortWithSpread_TPHit()
    {
        var trade = MakeShort(1.1000m, new Price(1.1030m), 0.0001m,
            (0, 5.0, -25.0),
            (60, 15.0, -50.0),
            (120, 20.0, -65.0));

        var rule = new ExitRule
        {
            SlAtrMultiple = 1.5,
            TpRrMultiple = 2.0,
            ReferenceAtrPips = 20.0,
        };

        var outcome = ExitReplayer.Replay(trade, rule);

        outcome.Kind.Should().Be(ExitKind.TP);
        outcome.BarsHeld.Should().Be(3);
        outcome.RMultiple.Should().BeApproximately(58.0 / 30.0, 0.01);
    }
}

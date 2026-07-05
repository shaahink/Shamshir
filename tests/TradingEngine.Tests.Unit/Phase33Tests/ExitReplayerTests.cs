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
}

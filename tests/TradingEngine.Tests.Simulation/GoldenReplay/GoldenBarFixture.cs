namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// A small, fully deterministic bar fixture for the golden replay oracle.
///
/// Design: EURUSD H1, 20 bars trending down 100 pips from 1.1000.
/// The AlwaysSignalStrategy fires Long after bar 6 (barCount > 5) with SL = close - 50 pips.
/// With a consistent 100-pip down trend over 20 bars, the SL gets hit around bar 16-17.
///
/// The BarBuilder.Trend method is deterministic (no random — only Range() uses Random).
/// None of the normalization is needed ON the bars themselves since Bar.OpenTimeUtc is fixed.
/// </summary>
public static class GoldenBarFixture
{
    public static readonly Symbol Symbol = Symbol.Parse("EURUSD");
    public static readonly Timeframe Timeframe = Timeframe.H1;

    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The canonical bar fixture: 20 EURUSD bars trending down 100 pips from 1.1000.</summary>
    public static IReadOnlyList<Bar> Create()
    {
        return Bars.Trend(Symbol, Timeframe, T0, 1.1000m, -100, 20).Build();
    }
}

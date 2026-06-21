using TradingEngine.Domain;

namespace TradingEngine.Services.AddOns;

/// <summary>
/// iter-38 (Stream A2). The market context the tuner needs, pre-computed by the caller (the entry seam in
/// <c>BarEvaluator</c>) so the tuner stays pure and trivially testable. <see cref="ReferenceAtrPips"/> is the
/// "typical" ATR for this symbol/timeframe (seed a small lookup, or derive from <c>SymbolInfo.TypicalSpread</c>
/// × a TF factor); when 0/unknown the volatility factor is neutral (1.0).
/// </summary>
public sealed record VolatilityContext(
    double AtrPips,
    double TypicalSpreadPips,
    double ReferenceAtrPips = 0);

/// <summary>The concrete add-on numbers the tuner produces. The <c>AddOnResolver</c> maps these onto the
/// option records of the enabled add-ons (Auto mode only).</summary>
public sealed record ResolvedAddOnValues(
    double TrailingAtrMultiple,
    double TrailingStepPips,
    double BreakevenTriggerR,
    double BreakevenOffsetPips,
    double PartialTpTriggerR,
    double PartialTpCloseFraction,
    double DynamicSlAtrMultiple,
    double DynamicTpRrMultiple,
    double RideAdxFloor,
    double RideRelaxedAtrMultiple);

/// <summary>
/// iter-38 (Stream A2 / owner decision D2). PURE, deterministic auto-tuner: given (timeframe, volatility) it
/// computes sensible add-on numbers so the SAME strategy gets different-but-reasonable management on a calm
/// H4 EURUSD vs a wild M15 USDJPY — with zero hand-tuning. Outputs are clamped and monotonic in TF + ATR.
///
/// These are STARTING heuristics (see docs/iterations/iter-38/PLAN.md §3). The agent calibrates the constants
/// against <c>AddOnAutoTunerTests</c> + the golden/characterization suites — but the SIGNATURE and the
/// clamp/monotonicity contract are fixed.
/// </summary>
public static class AddOnAutoTuner
{
    public static ResolvedAddOnValues Tune(Timeframe tf, VolatilityContext vol)
    {
        var tfBase = TrailingBaseFor(tf);
        var volFactor = vol.ReferenceAtrPips > 0
            ? Math.Clamp(vol.AtrPips / vol.ReferenceAtrPips, 0.7, 1.5)
            : 1.0;

        var trailingAtr = Math.Clamp(tfBase * volFactor, 1.5, 4.0);
        var trailingStep = Math.Max(0.5 * vol.AtrPips, 1.0);
        var beTrigger = Math.Clamp(1.0 / volFactor, 0.6, 1.6);   // calmer vol ⇒ arm breakeven sooner
        var beOffset = Math.Ceiling(vol.TypicalSpreadPips * 1.5) + 1;
        var dynSl = Math.Clamp(0.8 * tfBase, 1.0, 2.5);
        var dynTp = Math.Clamp(1.5 + 0.25 * TfTier(tf), 1.5, 3.0);

        return new ResolvedAddOnValues(
            TrailingAtrMultiple: trailingAtr,
            TrailingStepPips: trailingStep,
            BreakevenTriggerR: beTrigger,
            BreakevenOffsetPips: beOffset,
            PartialTpTriggerR: 1.0,
            PartialTpCloseFraction: 0.5,
            DynamicSlAtrMultiple: dynSl,
            DynamicTpRrMultiple: dynTp,
            RideAdxFloor: 25,
            RideRelaxedAtrMultiple: trailingAtr * 1.4);
    }

    private static double TrailingBaseFor(Timeframe tf) => tf switch
    {
        Timeframe.M1 or Timeframe.M5 or Timeframe.M15 => 2.0,
        Timeframe.M30 or Timeframe.H1 => 2.5,
        Timeframe.H4 => 3.0,
        Timeframe.D1 or Timeframe.W1 => 3.5,
        _ => 2.5,
    };

    private static int TfTier(Timeframe tf) => tf switch
    {
        Timeframe.M1 or Timeframe.M5 or Timeframe.M15 => 0,
        Timeframe.M30 or Timeframe.H1 => 1,
        Timeframe.H4 => 2,
        _ => 3,
    };
}

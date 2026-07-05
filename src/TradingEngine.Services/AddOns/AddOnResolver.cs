using TradingEngine.Domain;

namespace TradingEngine.Services.AddOns;

/// <summary>
/// iter-38 (Stream A3 / owner decision D2). Resolves a strategy's effective <see cref="PositionManagementOptions"/>
/// into the CONCRETE add-on values used for a position, applying the <see cref="AddOnAutoTuner"/> to every add-on
/// whose <c>Mode == Auto</c>. Designed to run ONCE at entry (deterministic — frozen for the position's life, so a
/// K6 replay reproduces identical numbers) and to feed <c>PositionManager.BuildConfig</c> unchanged downstream.
///
/// P3.4: when <c>Mode == Calibrated</c> and a calibration row is found, those values override the tuner; otherwise
/// falls back to Auto.
///
/// The returned <see cref="AddOnResolution.Raw"/> is what Stream A3 journals as an <c>ADDON_RESOLVED</c> record
/// (<see cref="AddOnJournalKinds"/>) so a run is self-describing.
/// </summary>
public sealed class AddOnResolver
{
    private readonly IExitCalibrationLookup? _calibrationLookup;

    public AddOnResolver(IExitCalibrationLookup? calibrationLookup = null)
    {
        _calibrationLookup = calibrationLookup;
    }

    public AddOnResolution ResolveAtEntry(
        PositionManagementOptions opts,
        string strategyId,
        string symbol,
        Timeframe tf,
        VolatilityContext vol)
    {
        var tuned = AddOnAutoTuner.Tune(tf, vol);
        var cal = _calibrationLookup?.Get(strategyId, symbol, tf, null);

        // Locals so nullable flow-analysis narrows cleanly (the repo builds with TreatWarningsAsErrors).
        var trailing = ResolveTrailing(opts.Trailing, tuned, cal);
        var breakeven = ResolveBreakeven(opts.Breakeven, tuned, cal);
        var partial = ResolvePartial(opts.PartialTp, tuned, cal);
        var ride = ResolveRide(opts.Ride, tuned, cal);

        // B6: DynamicSlTp is resolved per-bar in BarEvaluator (re-reads the raw PositionManagementOptions
        // off the strategy config every signal), NOT via BuildConfig/PositionManagementConfig (which has no
        // DynamicSlTp field). Resolving it here at entry is dead — BuildConfig silently drops it. Keeping
        // the raw opts.DynamicSlTp in the resolved record (untuned) lets BarEvaluator consume it correctly.
        var resolved = opts with
        {
            Trailing = trailing,
            Breakeven = breakeven,
            PartialTp = partial,
            Ride = ride,
        };

        return new AddOnResolution(resolved, tuned);
    }

    private static TrailingOptions ResolveTrailing(TrailingOptions t, ResolvedAddOnValues tuned, ExitCalibrationRecord? cal)
    {
        if (!t.Enabled) return t;
        if (t.Mode == AddOnMode.Custom) return t;
        if (t.Mode == AddOnMode.Calibrated && cal?.TrailAtrMultiple is { } cTrail)
            return t with { AtrMultiple = cTrail };
        return t with { AtrMultiple = tuned.TrailingAtrMultiple, StepPips = tuned.TrailingStepPips };
    }

    private static BreakevenOptions ResolveBreakeven(BreakevenOptions b, ResolvedAddOnValues tuned, ExitCalibrationRecord? cal)
    {
        if (!b.Enabled) return b;
        if (b.Mode == AddOnMode.Custom) return b;
        if (b.Mode == AddOnMode.Calibrated && cal?.BeTriggerR is { } cBe)
            return b with { TriggerRMultiple = cBe, OffsetPips = cal.BeOffsetPips ?? b.OffsetPips };
        return b with { TriggerRMultiple = tuned.BreakevenTriggerR, OffsetPips = tuned.BreakevenOffsetPips };
    }

    private static PartialTpOptions? ResolvePartial(PartialTpOptions? p, ResolvedAddOnValues tuned, ExitCalibrationRecord? cal)
    {
        if (p is not { Enabled: true }) return p;
        if (p.Mode == AddOnMode.Custom) return p;
        if (p.Mode == AddOnMode.Calibrated && cal?.PartialTriggerR is { } cTrigger)
            return p with { TriggerRMultiple = cTrigger, CloseFraction = cal.PartialCloseFraction ?? p.CloseFraction };
        return p with { TriggerRMultiple = tuned.PartialTpTriggerR, CloseFraction = tuned.PartialTpCloseFraction };
    }

    private static RideOptions? ResolveRide(RideOptions? r, ResolvedAddOnValues tuned, ExitCalibrationRecord? cal)
    {
        if (r is not { Enabled: true }) return r;
        if (r.Mode == AddOnMode.Custom) return r;
        // Calibrated: ride hasn't been added to ExitCalibrationRecord yet — P3 slot.
        if (r.Mode == AddOnMode.Calibrated)
            return r with { AdxFloor = tuned.RideAdxFloor, RelaxedAtrMultiple = tuned.RideRelaxedAtrMultiple };
        return r with { AdxFloor = tuned.RideAdxFloor, RelaxedAtrMultiple = tuned.RideRelaxedAtrMultiple };
    }
}

/// <summary>The resolved options to drive the position, plus the raw tuner output for journaling/preview.</summary>
public sealed record AddOnResolution(PositionManagementOptions Resolved, ResolvedAddOnValues Raw);

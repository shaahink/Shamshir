using TradingEngine.Domain;

namespace TradingEngine.Services.AddOns;

/// <summary>
/// iter-38 (Stream A3 / owner decision D2). Resolves a strategy's effective <see cref="PositionManagementOptions"/>
/// into the CONCRETE add-on values used for a position, applying the <see cref="AddOnAutoTuner"/> to every add-on
/// whose <c>Mode == Auto</c>. Designed to run ONCE at entry (deterministic — frozen for the position's life, so a
/// K6 replay reproduces identical numbers) and to feed <c>PositionManager.BuildConfig</c> unchanged downstream.
///
/// The returned <see cref="AddOnResolution.Raw"/> is what Stream A3 journals as an <c>ADDON_RESOLVED</c> record
/// (<see cref="AddOnJournalKinds"/>) so a run is self-describing.
/// </summary>
public sealed class AddOnResolver
{
    public AddOnResolution ResolveAtEntry(PositionManagementOptions opts, Timeframe tf, VolatilityContext vol)
    {
        var tuned = AddOnAutoTuner.Tune(tf, vol);

        // Locals so nullable flow-analysis narrows cleanly (the repo builds with TreatWarningsAsErrors).
        var trailing = opts.Trailing;
        if (trailing.Enabled && trailing.Mode == AddOnMode.Auto)
            trailing = trailing with { AtrMultiple = tuned.TrailingAtrMultiple, StepPips = tuned.TrailingStepPips };

        var breakeven = opts.Breakeven;
        if (breakeven.Enabled && breakeven.Mode == AddOnMode.Auto)
            breakeven = breakeven with { TriggerRMultiple = tuned.BreakevenTriggerR, OffsetPips = tuned.BreakevenOffsetPips };

        var partial = opts.PartialTp;
        if (partial is { Enabled: true, Mode: AddOnMode.Auto })
            partial = partial with { TriggerRMultiple = tuned.PartialTpTriggerR, CloseFraction = tuned.PartialTpCloseFraction };

        var ride = opts.Ride;
        if (ride is { Enabled: true, Mode: AddOnMode.Auto })
            ride = ride with { AdxFloor = tuned.RideAdxFloor, RelaxedAtrMultiple = tuned.RideRelaxedAtrMultiple };

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
}

/// <summary>The resolved options to drive the position, plus the raw tuner output for journaling/preview.</summary>
public sealed record AddOnResolution(PositionManagementOptions Resolved, ResolvedAddOnValues Raw);

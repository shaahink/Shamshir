namespace TradingEngine.Services.ExitLab;

/// <summary>
/// P3.3 / P4.5.3: pure function that replays a single exit rule against a single recorded excursion path.
/// Exit detection follows the venue's bar-processing order with decision-bar cadence for BE/trail updates:
/// (1) check SL/TP at CURRENT levels (set at the PREVIOUS decision bar's end),
/// (2) accumulate the running extreme within each decision-bar group,
/// (3) at the last point of a decision-bar group, apply BE/trail from that group's extreme.
/// 
/// Key venue-matching rules:
/// - SL-first-conservative: when both SL and TP hit the same bar, SL wins.
/// - Short-side spread: the venue detects short SL/TP on the ASK (bar shifted by full spread) and
///   fills short exits at ask. The replayer applies <c>SpreadPips</c> to short-side detection and fill
///   so cells aren't falsely optimistic ~one full spread.
/// - MAE tracks maximum adverse excursion (positive number = pips against the trade direction).
///   Favourable-only bars contribute zero adverse.
/// - Partial-TP: not yet modelled (split-R accounting required); Replay throws NotSupportedException
///   so an unmodelled rule isn't silently-wrong.
/// </summary>
public static class ExitReplayer
{
    public static ExitOutcome Replay(TradeExcursionInput trade, ExitRule rule)
    {
        if (rule.PartialTriggerR is not null || rule.PartialCloseFraction is not null)
        {
            throw new NotSupportedException(
                "Partial-TP is not yet modelled in the ExitReplayer — split-R accounting is required for correctness.");
        }

        var dir = trade.Direction == TradeDirection.Long ? 1 : -1;
        var riskPips = rule.SlAtrMultiple * rule.ReferenceAtrPips;
        var slPips = -dir * riskPips;

        var tpPips = rule.TpRrMultiple is { } tpMultiple and > 0
            ? dir * tpMultiple * riskPips
            : (double?)null;

        var spreadPips = trade.SpreadPips;
        var isShort = trade.Direction == TradeDirection.Short;
        var decisionTfMinutes = rule.DecisionTfMinutes;

        // BE config
        var beTargetPips = rule.BeTriggerR is { } beR and > 0
            ? dir * beR * riskPips
            : (double?)null;
        var beOffsetPips = rule.BeOffsetPips ?? 0;
        var beArmed = false;

        // Trailing state
        var trailDistPips = rule.TrailAtrMultiple is { } tMultiple and > 0
            ? tMultiple * rule.ReferenceAtrPips
            : (double?)null;

        var currentSlPips = slPips;
        var maePips = 0.0;
        var mfePips = 0.0;

        // P4.5.3b: bucket path points into decision-bar groups for BE/trail cadence.
        // The venue evaluates trailing/BE once per DECISION bar, AFTER the bar closes.
        // The replayer must: (1) apply BE/trail from the previous bar's accumulated extreme
        // BEFORE the exit check for the new bar, then (2) accumulate the current bar's extreme.
        int nextBeTrailUpdateMinute = decisionTfMinutes;
        double accumulatedFavorableExtreme = 0.0;
        bool accumulatedExtremeSet = false;

        var idx = 0;
        for (; idx < trade.Path.Count; idx++)
        {
            var point = trade.Path[idx];

            // P4.5.3a: short-side spread. The venue detects short SL/TP on the ASK (bar shifted by
            // full spread per the P0.2 convention) and fills short exits at ask. The recorded path
            // values are BID-relative (raw bar High/Low vs entry). For short detection, add spread.
            var barHiPips = isShort ? point.HiPips + spreadPips : point.HiPips;
            var barLoPips = isShort ? point.LoPips + spreadPips : point.LoPips;

            // Signed favorable extreme (for threshold comparisons and trailing level)
            var signedFavorableExtreme = dir > 0 ? barHiPips : barLoPips;

            // P4.5.3c: MAE tracks the maximum adverse excursion. adverseExtreme is computed as
            // a non-negative number (distance from entry in the wrong direction), then we take
            // the maximum via > comparison. Previous code used < against a zero start, which
            // inverted the result (favorable-only bars produced garbage negative values).
            var favorableExtreme = dir > 0 ? barHiPips : -barLoPips;
            var adverseExtreme = dir > 0 ? -barLoPips : barHiPips;
            if (adverseExtreme > maePips) maePips = adverseExtreme;
            if (favorableExtreme > mfePips) mfePips = favorableExtreme;

            // --- STEP 0: Apply BE/trail from PREVIOUS decision bar BEFORE exit check ---
            // Matches venue: TrailEvaluator runs after bar N closes, BEFORE bar N+1's exit evaluation.
            if (point.MinutesSinceEntry >= nextBeTrailUpdateMinute && accumulatedExtremeSet)
            {
                // Breakeven
                if (!beArmed && beTargetPips is { } beTgt
                    && (dir > 0 ? accumulatedFavorableExtreme >= beTgt : accumulatedFavorableExtreme <= beTgt))
                {
                    beArmed = true;
                    var beMove = beOffsetPips * dir;
                    currentSlPips = dir > 0
                        ? Math.Max(currentSlPips, beMove)
                        : Math.Min(currentSlPips, beMove);
                }

                // Trailing
                if (trailDistPips is { } trailD && trailD > 0)
                {
                    var trailLevel = dir > 0
                        ? accumulatedFavorableExtreme - trailD
                        : accumulatedFavorableExtreme + trailD;
                    currentSlPips = dir > 0
                        ? Math.Max(currentSlPips, trailLevel)
                        : Math.Min(currentSlPips, trailLevel);
                }

                accumulatedExtremeSet = false;
                nextBeTrailUpdateMinute += decisionTfMinutes;
            }

            // --- STEP 1: Exit detection at CURRENT stop/target levels ---
            var slHit = dir > 0
                ? barLoPips <= currentSlPips
                : barHiPips >= currentSlPips;

            var tpHit = tpPips is { } tpTgt && (dir > 0
                ? barHiPips >= tpTgt
                : barLoPips <= tpTgt);

            if (slHit)
            {
                var slExitPips = currentSlPips;
                if (isShort) slExitPips += spreadPips;
                var kind = currentSlPips == slPips ? ExitKind.SL :
                           beArmed ? ExitKind.Breakeven : ExitKind.TrailingStop;
                return MakeOutcome(kind, idx + 1, slExitPips, riskPips, dir, maePips, mfePips);
            }

            if (tpHit)
            {
                var tpExitPips = tpPips!.Value;
                if (isShort) tpExitPips += spreadPips;
                return MakeOutcome(ExitKind.TP, idx + 1, tpExitPips, riskPips, dir, maePips, mfePips);
            }

            // --- STEP 2: Accumulate this point's extreme for the CURRENT decision bar ---
            if (!accumulatedExtremeSet)
            {
                accumulatedFavorableExtreme = signedFavorableExtreme;
                accumulatedExtremeSet = true;
            }
            else
            {
                accumulatedFavorableExtreme = dir > 0
                    ? Math.Max(accumulatedFavorableExtreme, signedFavorableExtreme)
                    : Math.Min(accumulatedFavorableExtreme, signedFavorableExtreme);
            }
        }

        // End of data — close at last bar's adverse extreme.
        // Short: venue fills at ask (bar.High + spread). Long: fills at bid (bar.Low, raw).
        var closePips = trade.Path.Count > 0
            ? (isShort ? trade.Path[^1].HiPips + spreadPips : trade.Path[^1].LoPips)
            : 0.0;

        return MakeOutcome(ExitKind.EndOfData, trade.Path.Count, closePips, riskPips, dir, maePips, mfePips);
    }

    private static ExitOutcome MakeOutcome(ExitKind kind, int barsHeld, double rPips,
        double riskPips, int dir, double maePips, double mfePips) =>
        new()
        {
            Kind = kind,
            BarsHeld = barsHeld,
            RPips = rPips,
            RMultiple = rPips / riskPips * dir,
            MaePips = maePips,
            MfePips = mfePips,
        };
}

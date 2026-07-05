namespace TradingEngine.Services.ExitLab;

/// <summary>
/// P3.3: pure function that replays a single exit rule against a single recorded excursion path.
/// Exit detection follows the venue's bar-processing order: (1) check SL/TP at CURRENT levels,
/// (2) if neither hit, THEN update BE/trail from this bar's extreme. SL-first-conservative:
/// when both SL and TP hit the same bar, SL wins.
/// </summary>
public static class ExitReplayer
{
    public static ExitOutcome Replay(TradeExcursionInput trade, ExitRule rule)
    {
        var dir = trade.Direction == TradeDirection.Long ? 1 : -1;
        var riskPips = rule.SlAtrMultiple * rule.ReferenceAtrPips;
        var slPips = -dir * riskPips;

        var tpPips = rule.TpRrMultiple is { } tpMultiple and > 0
            ? dir * tpMultiple * riskPips
            : (double?)null;

        // BE config
        var beTargetPips = rule.BeTriggerR is { } beR and > 0
            ? dir * beR * riskPips
            : (double?)null;
        var beOffsetPips = rule.BeOffsetPips ?? 0;
        var beArmed = false;

        // Partial TP
        var partialTargetPips = rule.PartialTriggerR is { } pr and > 0
            && rule.PartialCloseFraction is { } pfrac and > 0
            ? dir * pr * riskPips
            : (double?)null;
        var partialFraction = rule.PartialCloseFraction ?? 0;
        var partialFired = false;

        // Trailing state
        var trailDistPips = rule.TrailAtrMultiple is { } tMultiple and > 0
            ? tMultiple * rule.ReferenceAtrPips
            : (double?)null;

        var currentSlPips = slPips;
        var tradeSize = 1.0;
        var maePips = 0.0;
        var mfePips = 0.0;

        var idx = 0;
        for (; idx < trade.Path.Count; idx++)
        {
            var point = trade.Path[idx];
            var favorableExtreme = dir > 0 ? point.HiPips : -point.LoPips;
            var adverseExtreme = dir > 0 ? -point.LoPips : point.HiPips;

            if (adverseExtreme < maePips) maePips = adverseExtreme;
            if (favorableExtreme > mfePips) mfePips = favorableExtreme;

            // --- STEP 1: Exit detection at CURRENT stop/target levels ---
            var slHit = dir > 0
                ? point.LoPips <= currentSlPips
                : point.HiPips >= currentSlPips;

            var tpHit = tpPips is { } tpTgt && (dir > 0
                ? point.HiPips >= tpTgt               // long: bar high crosses TP above entry
                : point.LoPips <= tpTgt);             // short: bar low crosses TP below entry (both negative)

            if (slHit)
            {
                var kind = currentSlPips == slPips ? ExitKind.SL :
                           beArmed ? ExitKind.Breakeven : ExitKind.TrailingStop;
                return MakeOutcome(kind, idx + 1, currentSlPips, riskPips, dir, maePips, mfePips);
            }

            if (tpHit)
            {
                return MakeOutcome(ExitKind.TP, idx + 1, tpPips!.Value, riskPips, dir, maePips, mfePips);
            }

            // --- STEP 2: No exit — update BE / trail / partial from this bar ---
            // Partial TP (before BE/trail so partial fires on the triggering bar)
            if (!partialFired && partialTargetPips is { } pTgt && favorableExtreme >= pTgt)
            {
                tradeSize -= partialFraction;
                partialFired = true;
                currentSlPips = Math.Max(currentSlPips, beOffsetPips * dir);
            }

            // Breakeven
            if (!beArmed && beTargetPips is { } beTgt && favorableExtreme >= beTgt)
            {
                beArmed = true;
                currentSlPips = Math.Max(currentSlPips, beOffsetPips * dir);
            }

            // Trailing: move SL up to favExtreme - trailDist, but only tighten (Math.Max)
            if (trailDistPips is { } trailD && trailD > 0)
            {
                var trailLevel = favorableExtreme - dir * trailD;
                currentSlPips = Math.Max(currentSlPips, trailLevel);
            }
        }

        // End of data — close at last bar's adverse extreme (bid/long = bar low, ask/short = bar high)
        var closePips = trade.Path.Count > 0
            ? (dir > 0 ? trade.Path[^1].LoPips : -trade.Path[^1].HiPips)
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

namespace TradingEngine.Strategies.Sessions;

/// <summary>
/// A UTC time-of-day window <c>[StartUtc, EndUtc)</c> — start-inclusive, end-exclusive — that correctly
/// handles an overnight wrap (<c>EndUtc &lt; StartUtc</c>, e.g. 22:00–06:00 spans midnight). This is the
/// V4 session family's single source of truth for "is this bar's open time inside my window", so the four
/// clock-keyed strategies don't each copy-paste the fiddly wrap compare (4× duplication is where the
/// F78-class bugs hide).
///
/// The comparison is on the bar's OPEN time-of-day, matching the engine's evaluation convention
/// (<c>BarEvaluator</c> anchors <c>MarketContext.EngineTimeUtc</c> to the just-closed bar's open time).
/// </summary>
public readonly record struct SessionWindow(TimeOnly StartUtc, TimeOnly EndUtc)
{
    /// <summary>True when <paramref name="utc"/>'s time-of-day falls in <c>[StartUtc, EndUtc)</c>,
    /// wrapping across midnight when <see cref="EndUtc"/> is earlier than <see cref="StartUtc"/>.</summary>
    public bool Contains(DateTime utc)
    {
        var t = TimeOnly.FromDateTime(utc);

        // Non-wrapping window (the common case, incl. degenerate empty when Start == End).
        if (EndUtc >= StartUtc)
            return t >= StartUtc && t < EndUtc;

        // Overnight wrap: the window spans midnight, so membership is the UNION of the two half-open legs.
        return t >= StartUtc || t < EndUtc;
    }
}

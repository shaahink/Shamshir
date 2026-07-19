namespace TradingEngine.Strategies.Sessions;

/// <summary>
/// Computes the opening-range <c>(High, Low)</c> over the entry-timeframe bars whose <c>OpenTimeUtc</c>
/// falls inside a build <see cref="SessionWindow"/> on a given calendar day. Mirrors
/// <c>SessionBreakoutStrategy</c>'s range-build (max High / min Low over the in-window bars), but windowed
/// by <see cref="SessionWindow.Contains"/> so the wrap logic lives in exactly one place.
///
/// Stateless by design: <see cref="Compute"/> recomputes from the bars it is handed each call (the same
/// list the strategy already holds via <c>MarketContext.Bars</c>), so there is no accumulated cross-call
/// state that could drift or hold a stale prior-day range. Owning strategies call it only during their
/// ENTRY window, by which point the build window is complete, so the returned range is always the full
/// range for the day — never a partial. <see cref="Reset"/> exists for lifecycle symmetry with the owning
/// strategy and is a no-op.
/// </summary>
public sealed class OpeningRangeTracker
{
    public OpeningRangeTracker(SessionWindow buildWindow) => BuildWindow = buildWindow;

    /// <summary>The build window this tracker measures the opening range over.</summary>
    public SessionWindow BuildWindow { get; }

    /// <summary>The opening range over <paramref name="bars"/> whose open time is inside the build window
    /// AND on <paramref name="sessionDayUtc"/>'s calendar day, or <c>null</c> if no such bar exists yet.</summary>
    public (decimal High, decimal Low)? Compute(IReadOnlyList<Bar> bars, DateTime sessionDayUtc)
    {
        var day = sessionDayUtc.Date;
        decimal? high = null;
        decimal? low = null;

        foreach (var bar in bars)
        {
            if (bar.OpenTimeUtc.Date != day)
                continue;
            if (!BuildWindow.Contains(bar.OpenTimeUtc))
                continue;

            high = high is null ? bar.High : Math.Max(high.Value, bar.High);
            low = low is null ? bar.Low : Math.Min(low.Value, bar.Low);
        }

        if (high is null || low is null)
            return null;

        return (high.Value, low.Value);
    }

    /// <summary>No-op — the tracker holds no cross-call state (see class remarks). Present so a strategy's
    /// <c>Reset()</c> can call it uniformly alongside its own state.</summary>
    public void Reset()
    {
    }
}

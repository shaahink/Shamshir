namespace TradingEngine.Host;

/// <summary>
/// The prop-firm reset clock (iter-36 NEW-1 / K-GAP-1). Given the previous and current bar sim-times (UTC)
/// and the configured reset boundary, it returns which of the day / week / month boundaries were crossed in
/// the half-open interval <c>(prev, current]</c>. The <c>KernelBacktestLoop</c> turns each crossed boundary
/// into a <c>DayRolled</c> / <c>WeekRolled</c> / <c>MonthRolled</c> event so the pure reducer re-bases
/// drawdown to the new period's opening equity and resets the governor (fixes C4 + H7 for multi-day runs —
/// before this, the production loop never emitted a roll, so a multi-day backtest never reset).
///
/// PURE + fully deterministic for the default UTC zone (identity conversion). Non-UTC zones use the OS time
/// -zone database via <see cref="TimeZoneInfo"/> and fall back to UTC when the id is unknown, so a missing
/// zone degrades gracefully rather than throwing (see <see cref="Crossed"/>'s determinism note).
///
/// iter-38 B0: moved out of <c>TradingEngine.Engine</c> into the Host so the Engine assembly stays free of
/// <c>DateTime</c>/<c>TimeOnly</c> (the <c>EnginePurityTests</c> architecture gate). It was always called
/// from the Host (<c>KernelBacktestLoop</c>/<c>EngineRunner</c>), so this is a pure relocation.
/// </summary>
public static class ResetClock
{
    /// <summary>Which reset boundaries fired on a bar. <see cref="Day"/> is also true whenever the week/month
    /// boundary fires (a month start is also a day start), so callers emit the most-specific events they want.</summary>
    public readonly record struct RollFlags(bool Day, bool Week, bool Month)
    {
        public bool Any => Day || Week || Month;
        public static RollFlags None => default;
    }

    /// <summary>
    /// The reset boundaries falling in <c>(prevSimUtc, currentSimUtc]</c>. The first bar of a run
    /// (<paramref name="prevSimUtc"/> == null) crosses nothing — the run's initial state IS the first
    /// period's baseline. A gap that skips whole periods (e.g. a weekend) reports a SINGLE crossing per kind:
    /// the reducer re-bases to the *current* equity and the governor reset is idempotent, so collapsing
    /// repeated resets with no trading in between is both correct and simpler.
    ///
    /// Determinism: for the default <c>"UTC"</c> zone this is a pure function of its inputs. Non-UTC zones
    /// depend on the host's tz database (DST rules can differ across OS versions) — acceptable today because
    /// the seeded prop-firm rulesets use UTC; harden with an embedded tz provider if a non-UTC firm is added.
    /// </summary>
    public static RollFlags Crossed(DateTime? prevSimUtc, DateTime currentSimUtc, ResetConfig cfg)
    {
        if (prevSimUtc is null) return RollFlags.None;
        if (currentSimUtc <= prevSimUtc.Value) return RollFlags.None;

        var tz = ResolveZone(cfg.Timezone);
        var prevLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(prevSimUtc.Value, DateTimeKind.Utc), tz);
        var curLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(currentSimUtc, DateTimeKind.Utc), tz);

        var prevPeriod = ResetPeriodDate(prevLocal, cfg.DailyResetTime);
        var curPeriod = ResetPeriodDate(curLocal, cfg.DailyResetTime);

        var day = curPeriod > prevPeriod;
        var week = WeekAnchor(curPeriod, cfg.WeekStartsOn) > WeekAnchor(prevPeriod, cfg.WeekStartsOn);
        var month = new DateOnly(curPeriod.Year, curPeriod.Month, 1) > new DateOnly(prevPeriod.Year, prevPeriod.Month, 1);

        return new RollFlags(day, week, month);
    }

    /// <summary>
    /// The reset-period date a local instant belongs to: the date whose reset boundary most recently started
    /// at or before it. Before today's reset time you are still in yesterday's period (e.g. with a 22:00
    /// reset, 21:59 belongs to yesterday's period; 22:00 starts today's — the boundary is inclusive).
    /// </summary>
    private static DateOnly ResetPeriodDate(DateTime local, TimeOnly resetTime)
    {
        var date = DateOnly.FromDateTime(local);
        return TimeOnly.FromDateTime(local) >= resetTime ? date : date.AddDays(-1);
    }

    /// <summary>The week-start date (anchored on <paramref name="weekStartsOn"/>) for a reset-period date.</summary>
    private static DateOnly WeekAnchor(DateOnly periodDate, DayOfWeek weekStartsOn)
    {
        var delta = ((int)periodDate.DayOfWeek - (int)weekStartsOn + 7) % 7;
        return periodDate.AddDays(-delta);
    }

    private static TimeZoneInfo ResolveZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "UTC" or "Etc/UTC") return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Utc; }
    }
}

/// <summary>
/// The resolved reset boundary (run-constant). Built from a <c>PropFirmRuleSet</c>'s daily-reset time +
/// timezone; the week boundary defaults to Monday (FTMO-style). Kept a small value type so the loop can hold
/// it without coupling the Engine project to the Domain ruleset.
/// </summary>
public readonly record struct ResetConfig(TimeOnly DailyResetTime, string Timezone, DayOfWeek WeekStartsOn = DayOfWeek.Monday)
{
    /// <summary>Parse from a <c>PropFirmRuleSet</c>'s "HH:mm:ss" reset-time string + tz id. An unparseable
    /// time falls back to midnight; an empty zone falls back to UTC.</summary>
    public static ResetConfig FromRuleSet(string? dailyResetTime, string? timezone, DayOfWeek weekStartsOn = DayOfWeek.Monday)
    {
        var time = TimeOnly.TryParse(dailyResetTime, out var t) ? t : new TimeOnly(0, 0);
        return new ResetConfig(time, string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone, weekStartsOn);
    }
}

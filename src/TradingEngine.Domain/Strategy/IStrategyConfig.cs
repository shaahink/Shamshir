namespace TradingEngine.Domain;

public interface IStrategyConfig
{
    string Id { get; }
    string DisplayName { get; }
    bool Enabled { get; }
    string RiskProfileId { get; }
    RegimeFilterOptions RegimeFilter { get; }
    OrderEntryOptions OrderEntry { get; }
    PositionManagementOptions PositionManagement { get; }
    ReentryOptions Reentry { get; }
    EntryFilterOptions? EntryFilter => null;
    Timeframe EntryTimeframe { get; }
    string? Symbol { get; }
    IReadOnlyList<Timeframe> RequiredTimeframes { get; }

    /// <summary>P2.4/D6: when set, the loop force-closes every OPEN position this strategy holds at the
    /// first bar whose time-of-day reaches this value (daily) — the building block for FTMO daily-DD
    /// hygiene and P7's news/weekend flattening. Null (default) ⇒ no time-flatten behavior.</summary>
    TimeOnly? FlattenAtUtc => null;

    bool FlattenBeforeWeekend => false;

    int? FlattenBeforeNewsMinutes => null;
}


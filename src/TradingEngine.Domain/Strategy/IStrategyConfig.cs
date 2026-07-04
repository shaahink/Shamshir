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
    Timeframe EntryTimeframe { get; }
    string? Symbol { get; }
    IReadOnlyList<Timeframe> RequiredTimeframes { get; }
}


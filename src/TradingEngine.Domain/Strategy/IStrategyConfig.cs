namespace TradingEngine.Domain;

public interface IStrategyConfig
{
    string Id { get; }
    string DisplayName { get; }
    bool Enabled { get; }
    IReadOnlyList<string> Symbols { get; }
    string RiskProfileId { get; }
    Timeframe Timeframe { get; }
    RegimeFilterOptions RegimeFilter { get; }
    OrderEntryOptions OrderEntry { get; }
}


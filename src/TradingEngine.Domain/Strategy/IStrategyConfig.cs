namespace TradingEngine.Domain;

public interface IStrategyConfig
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<string> Symbols { get; }
    string RiskProfileId { get; }
    Timeframe Timeframe { get; }
}


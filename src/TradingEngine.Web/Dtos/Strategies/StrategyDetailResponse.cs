namespace TradingEngine.Web.Dtos.Strategies;

public sealed record StrategyDetailResponse
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; }
    public required string Timeframe { get; init; }
    public string[] Symbols { get; init; } = [];
    public string? ParametersJson { get; init; }
    public string? PositionManagementJson { get; init; }
    public string? OrderEntryJson { get; init; }
    public string? RegimeFilterJson { get; init; }
}

namespace TradingEngine.Web.Dtos.Strategies;

public sealed record StrategyUpdateRequest
{
    public string? DisplayName { get; init; }
    public string? ParametersJson { get; init; }
    public string? PositionManagementJson { get; init; }
    public string? OrderEntryJson { get; init; }
    public string? RegimeFilterJson { get; init; }
}

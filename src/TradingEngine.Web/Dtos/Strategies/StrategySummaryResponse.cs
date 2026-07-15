namespace TradingEngine.Web.Dtos.Strategies;

public sealed record StrategySummaryResponse
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; }
    public required string Timeframe { get; init; }
    public string[] Symbols { get; init; } = [];
}

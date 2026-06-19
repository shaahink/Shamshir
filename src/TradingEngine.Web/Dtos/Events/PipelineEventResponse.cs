namespace TradingEngine.Web.Dtos.Events;

public sealed record PipelineEventResponse
{
    public long Seq { get; init; }
    public DateTime SimTimeUtc { get; init; }
    public string? Kind { get; init; }
    public string? Symbol { get; init; }
    public string? StrategyId { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }
}

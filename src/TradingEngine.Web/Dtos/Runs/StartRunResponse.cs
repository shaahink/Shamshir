namespace TradingEngine.Web.Dtos.Runs;

public sealed record StartRunResponse
{
    public required string RunId { get; init; }
    public required string Status { get; init; }
}

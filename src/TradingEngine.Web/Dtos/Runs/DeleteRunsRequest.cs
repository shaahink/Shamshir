namespace TradingEngine.Web.Dtos.Runs;

public sealed record DeleteRunsRequest
{
    public List<string>? RunIds { get; init; }
}

public sealed record PruneRunsRequest
{
    public int Keep { get; init; } = 20;
}

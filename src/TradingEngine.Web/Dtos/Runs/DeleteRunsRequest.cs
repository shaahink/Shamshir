namespace TradingEngine.Web.Dtos.Runs;

/// <summary>M4.1 (E2): multi-select delete of finished runs.</summary>
public sealed record DeleteRunsRequest
{
    public List<string>? RunIds { get; init; }
}

/// <summary>M4.1 (E2): keep the newest <see cref="Keep"/> runs, delete the rest (active runs always kept).</summary>
public sealed record PruneRunsRequest
{
    public int Keep { get; init; } = 20;
}

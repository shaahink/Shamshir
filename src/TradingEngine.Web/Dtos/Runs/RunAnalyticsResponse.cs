namespace TradingEngine.Web.Dtos.Runs;

public sealed record RunAnalyticsResponse
{
    public List<double> RMultiples { get; init; } = [];
    public List<double> HoldingTimes { get; init; } = [];
    public List<AnalyticsBucket> PnlByHour { get; init; } = [];
    public List<AnalyticsBucket> PnlByDay { get; init; } = [];
    public List<MaeMfePoint> MaeMfe { get; init; } = [];
}

public sealed record AnalyticsBucket
{
    public required string Key { get; init; }
    public double Value { get; init; }
}

public sealed record MaeMfePoint
{
    public double X { get; init; }
    public double Y { get; init; }
}

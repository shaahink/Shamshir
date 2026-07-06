namespace TradingEngine.Web.Dtos.Runs;

public sealed record CompareBothRequest
{
    public string ConfigName { get; init; } = "";
}

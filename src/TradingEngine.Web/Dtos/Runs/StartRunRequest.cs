namespace TradingEngine.Web.Dtos.Runs;

public sealed record StartRunRequest
{
    public string Symbol { get; init; } = "EURUSD";
    public string Period { get; init; } = "h1";
    public DateTime Start { get; init; } = new(2024, 1, 1);
    public DateTime End { get; init; } = new(2024, 1, 31);
    public decimal Balance { get; init; } = 100_000;
    public double CommissionPerMillion { get; init; } = 30;
    public double SpreadPips { get; init; } = 1;
    public string? Symbols { get; init; }
    public string? Periods { get; init; }
    public string? StrategyIds { get; init; }
}

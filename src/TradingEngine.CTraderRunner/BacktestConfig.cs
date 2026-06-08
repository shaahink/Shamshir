namespace TradingEngine.CTraderRunner;

public sealed record BacktestConfig
{
    public string RunId { get; init; } = "";
    public required string Symbol { get; init; }
    public required string Period { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public decimal Balance { get; init; } = 100_000;
    public double CommissionPerMillion { get; init; } = 30;
    public double SpreadPips { get; init; } = 1;
    public string DataMode { get; init; } = "m1";
    public string? DataFile { get; init; }
    public string? DataDir { get; init; }
    public bool UseFullAccess { get; init; } = true;
    public string[] Symbols { get; init; } = ["EURUSD"];
    public string[] Periods { get; init; } = ["H1"];
    public Dictionary<string, string> CustomParams { get; init; } = new();
}

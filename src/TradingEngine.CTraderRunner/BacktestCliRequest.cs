namespace TradingEngine.CTraderRunner;

public sealed record BacktestCliRequest
{
    public required string AlgoPath { get; init; }
    public required string Symbol { get; init; }
    public required string Period { get; init; }
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public required string CtId { get; init; }
    public required string PwdFile { get; init; }
    public required string Account { get; init; }
    public required int DataPort { get; init; }
    public required int CommandPort { get; init; }

    public decimal Balance { get; init; } = 100_000m;
    public decimal CommissionPerMillion { get; init; } = 30m;
    public decimal SpreadPips { get; init; } = 1m;
    public string DataMode { get; init; } = "m1";

    public IReadOnlyList<string> Symbols { get; init; } = [];
    public IReadOnlyList<string> Periods { get; init; } = [];
    public bool FullAccess { get; init; } = true;

    public string? ReportJsonPath { get; init; }
    public string? DataDir { get; init; }
    public string? DataFile { get; init; }
}

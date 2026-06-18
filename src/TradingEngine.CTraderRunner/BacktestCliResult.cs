namespace TradingEngine.CTraderRunner;

public sealed record BacktestCliResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public bool IsKnownCrash { get; init; }
    public string? ReportJsonPath { get; init; }
    public List<string> CbotLines { get; init; } = [];
}

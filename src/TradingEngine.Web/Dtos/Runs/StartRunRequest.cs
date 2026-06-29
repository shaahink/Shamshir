namespace TradingEngine.Web.Dtos.Runs;

public sealed record StartRunRequest
{
    public DateTime Start { get; init; } = new(2024, 1, 1);
    public DateTime End { get; init; } = new(2024, 1, 31);
    public decimal Balance { get; init; } = 100_000;
    public double CommissionPerMillion { get; init; } = 30;
    public double SpreadPips { get; init; } = 1;
    public List<string>? Symbols { get; init; }
    public List<string>? Periods { get; init; }
    public List<string>? StrategyIds { get; init; }

    public string? RiskProfileId { get; init; }
    public string? Venue { get; init; }
    public Dictionary<string, Dictionary<string, object>>? StrategyOverrides { get; init; }
    public string? UsePackId { get; init; }
    public Dictionary<string, string>? PerStrategyPackIds { get; init; }
    public bool DisableRegime { get; init; }

    // iter-strategy-system P1 (D3/D4): the row-based builder. When Rows is present it supersedes the
    // Symbols×Periods×StrategyIds cross-product — each row is an explicit (strategy × symbol × timeframe)
    // with its own add-on pack. Risk profile, governor and money stay run-level (the fields above).
    public List<RunRowRequest>? Rows { get; init; }

    // Run-level governor toggle (D4). Default true = governor on, as today. False disables the governor
    // for the whole run (GovernorOptions.Enabled = false).
    public bool GovernorEnabled { get; init; } = true;
}

public sealed record RunRowRequest
{
    public string StrategyId { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string Timeframe { get; init; } = "";
    public string? PackId { get; init; }
    public bool Enabled { get; init; } = true;
}

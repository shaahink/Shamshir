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
}

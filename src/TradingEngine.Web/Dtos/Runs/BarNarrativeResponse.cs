namespace TradingEngine.Web.Dtos.Runs;

/// <summary>
/// Per-bar decision narrative (iter-redesign P5) — answers "what did the engine see, decide, and
/// why" for one bar. Aggregated from the existing persisted journal; no new DB table required.
/// </summary>
public sealed record BarNarrativeResponse
{
    public DateTime SimTimeUtc { get; init; }
    public long FirstSeq { get; init; }
    public int EventCount { get; init; }
    public string? Regime { get; init; }
    public List<BarStrategyVerdictDto> Verdicts { get; init; } = [];
    public int ProposalCount { get; init; }
    public List<string> GateRejections { get; init; } = [];
    public BarRiskSnapshotDto? Risk { get; init; }
    public int FillCount { get; init; }
    public int CloseCount { get; init; }
    public int RejectionCount { get; init; }
}

public sealed record BarStrategyVerdictDto
{
    public string StrategyId { get; init; } = "";
    public bool SignalFired { get; init; }
    public string? Direction { get; init; }
    public string Reason { get; init; } = "";
}

public sealed record BarRiskSnapshotDto
{
    public decimal Equity { get; init; }
    public decimal Balance { get; init; }
    public decimal DailyDrawdown { get; init; }
    public decimal MaxDrawdown { get; init; }
    public int OpenPositions { get; init; }
    public bool InProtectionMode { get; init; }
    public string GovernorState { get; init; } = "Normal";
}

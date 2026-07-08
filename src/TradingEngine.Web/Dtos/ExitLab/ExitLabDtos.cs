namespace TradingEngine.Web.Dtos.ExitLab;

/// <summary>P3.5 — request to evaluate a grid of exit rules against recorded excursion paths.</summary>
public sealed record ExitLabEvaluateRequest
{
    public required List<string> RunIds { get; init; }
    public required List<Guid> PositionIds { get; init; }
    public required double ReferenceAtrPips { get; init; }
    public double[]? SlMultiples { get; init; }
    public double?[]? TpMultiples { get; init; }
    public double?[]? BeTriggers { get; init; }
    public double?[]? TrailMultiples { get; init; }
    // P6.4: optional trading-session regime filter — when set, only trades whose entry bar falls in this
    // session are evaluated (SessionDetector labels: Asian, London, NewYork, Asian-London, London-NY, Pacific).
    public string? Regime { get; init; }
}

public sealed record ExitLabCellResponse
{
    public required TradingEngine.Services.ExitLab.ExitRule Rule { get; init; }
    public int TradeCount { get; init; }
    public double WinRate { get; init; }
    public double AvgR { get; init; }
    public double MedianR { get; init; }
    public double AvgHoldBars { get; init; }
    public double MaxDdContributionR { get; init; }
    public System.Collections.Generic.IReadOnlyList<double> TradeRValues { get; init; } = [];
    public double PassProbability { get; init; }
    // P4.5.7: plateau-center badge so the UI can highlight the cell you should actually pick
    // (mid-plateau, not the isolated peak — anti-overfit per QUANT-ROADMAP §3.2).
    public bool IsPlateauCenter { get; init; }
}

public sealed record ExitLabEvaluateResponse
{
    public int TotalTrades { get; init; }
    public int TotalCells { get; init; }
    public int MalformedPathCount { get; init; }
    public required List<ExitLabCellResponse> Cells { get; init; }
    public double[]? DefaultSlMultiples { get; init; }
    public double?[]? DefaultTpMultiples { get; init; }
    // P6.4: regime-conditioned calibration — the regime filter that was applied (null = all sessions).
    public string? Regime { get; init; }
    // P6.4: trade-count breakdown by trading session (SessionDetector labels). Provided even when
    // a single regime is selected so the caller can see the data distribution across sessions.
    public Dictionary<string, int>? RegimeBreakdown { get; init; }
}

/// <summary>P3.4 — save a calibrated exit rule for a (strategy, symbol, timeframe, regime) cell.</summary>
public sealed record SaveCalibrationRequest
{
    public required string StrategyId { get; init; }
    public required string Symbol { get; init; }
    public required string EntryTimeframe { get; init; }
    public string? Regime { get; init; }
    public required TradingEngine.Services.ExitLab.ExitRule Rule { get; init; }
    public required string DatasetId { get; init; }
    public required DateTime IsStartUtc { get; init; }
    public required DateTime IsEndUtc { get; init; }
    public DateTime? OosStartUtc { get; init; }
    public DateTime? OosEndUtc { get; init; }
}

namespace TradingEngine.Domain;

public sealed record DecisionRecord(
    string RunId,
    DateTime SimTimeUtc,
    long Seq,
    string? Symbol,
    string? StrategyId,
    string? PhaseBefore,
    string Event,
    string? GuardResult,
    string? PhaseAfter,
    string? Reason,
    string DetailJson);

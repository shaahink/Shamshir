namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// The serialized output of a golden replay run. This is the committed baseline that every subsequent
/// kernel change must produce bit-identically (after normalization). Captures everything the engine
/// currently outputs: ordered trades, equity/drawdown, and every decision-journal line.
/// </summary>
public sealed record GoldenSnapshot(
    int BarCount,
    IReadOnlyList<GoldenTrade> Trades,
    IReadOnlyList<GoldenJournalEntry> Journal,
    GoldenRiskState FinalRisk);

public sealed record GoldenTrade(
    string Direction,
    decimal Lots,
    decimal EntryPrice,
    decimal ExitPrice,
    string ExitReason);

public sealed record GoldenJournalEntry(
    string Stage,
    string Event,
    string? GuardResult,
    string? Reason);

public sealed record GoldenRiskState(
    decimal PeakEquity,
    decimal CurrentDailyDrawdown,
    decimal CurrentMaxDrawdown,
    bool InProtectionMode,
    string? ProtectionCause);

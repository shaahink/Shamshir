namespace TradingEngine.Domain;

/// <summary>
/// One append-only record of <b>exactly what happened on one kernel step</b> — the unit of the
/// unified journal (iter-35 A3). The kernel produces a <see cref="StepRecord"/> for every
/// <see cref="EngineEvent"/> it processes: the input event, the gate/decision verdict, the effects it
/// emitted, and a snapshot of risk/regime at that instant.
///
/// This single stream <b>replaces</b> the four overlapping observability sinks (PipelineEvents,
/// BarEvaluations, logger lines, SignalR progress — NEW-4). It is:
///   • lossless        — written through a <c>Wait</c> channel, never DropOldest (fixes C9/H17/H19);
///   • human-readable   — rendered to text / downloadable as NDJSON (one StepRecord per line);
///   • machine-queryable— persisted to the Journal table, SQL-paged by <see cref="Seq"/>;
///   • replay-anchored  — keyed by <see cref="Seq"/> + <see cref="SimTimeUtc"/> (never wall-clock),
///                        so a deterministic re-run reproduces it bit-for-bit.
/// </summary>
public sealed record StepRecord(
    string RunId,
    long Seq,
    DateTime SimTimeUtc,
    string EventKind,
    string EventJson,
    IReadOnlyList<string> EffectKinds,
    string EffectsJson,
    RiskSnapshot Risk,
    string? Regime,
    string? DecisionReason,
    IReadOnlyList<StrategyVerdict> StrategyVerdicts);

/// <summary>Risk/equity at the moment of a step. The live monitor renders this; the report charts it.</summary>
public sealed record RiskSnapshot(
    decimal Balance,
    decimal Equity,
    decimal FloatingPnL,
    decimal DailyDrawdown,
    decimal MaxDrawdown,
    decimal WeeklyDrawdown,
    decimal MonthlyDrawdown,
    bool InProtectionMode,
    string? ProtectionCause,
    string GovernorState,
    int OpenPositions);

/// <summary>
/// Per-strategy evaluation outcome for a bar — answers OBS-02 ("why was the signal rejected at each
/// bar?"). <see cref="Indicators"/> is included only per the journal sampling policy (PLAN Q5): always
/// when a signal fired, otherwise on a configurable stride, so the journal stays lossless-where-it-
/// matters without exploding the DB.
/// </summary>
public sealed record StrategyVerdict(
    string StrategyId,
    bool HadEnoughBars,
    bool SignalFired,
    TradeDirection? Direction,
    string Reason,
    IReadOnlyDictionary<string, double>? Indicators);

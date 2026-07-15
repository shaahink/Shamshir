namespace TradingEngine.Domain;

/// <summary>
/// Replay model (iter-35 A1/A4). A <see cref="RunSpec"/> is the full, reproducible definition of a
/// backtest: <c>(DatasetRef, ConfigSet, Seed)</c>. Identical specs ⇒ bit-identical journals (the
/// determinism guarantee that is the system's strongest correctness oracle).
///
///   • <see cref="DatasetRef"/> — content-addressed market data. Re-running a different ConfigSet over
///     the SAME DatasetId/ContentHash is "repeat this backtest with a different strategy / risk".
///   • <see cref="ConfigSet"/>  — immutable snapshot of EVERYTHING that determines behavior
///     (strategy configs + risk profile + prop-firm ruleset + governor + sizing + regime + rotation +
///     news). Captured at run start, hashed, persisted. Extends the existing EffectiveConfig capture.
///   • <see cref="RunSpec.Seed"/> — seeds any deterministic id/ordering source so replay is exact
///     (see NEW-10: the reducer must be pure; nondeterminism is seeded or tape-derived).
/// </summary>
public enum DatasetGranularity { Bar, Tick }

public sealed record DatasetRef(
    string DatasetId,
    string ContentHash,
    IReadOnlyList<string> Symbols,
    IReadOnlyList<string> Timeframes,
    DateTime FromUtc,
    DateTime ToUtc,
    DatasetGranularity Granularity,
    long RowCount);

public sealed record ConfigSet(
    string ConfigSetId,
    string ContentHash,
    string Json);

public sealed record RunSpec(
    string RunId,
    DatasetRef Dataset,
    ConfigSet Config,
    int Seed);

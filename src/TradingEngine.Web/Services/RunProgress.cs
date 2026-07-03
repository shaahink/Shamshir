namespace TradingEngine.Web.Services;

/// <summary>
/// The throttled live-run progress envelope pushed over SignalR (<see cref="Hubs.RunHub"/>).
/// Its JSON shape is the contract the Run Monitor (iter-21 U2) consumes, deliberately shaped to
/// match iter-20's future <c>RunProjection</c>/<c>DecisionRecord</c>/<c>AccountSnapshot</c> so that
/// when the kernel is wired (iter-20 P7) only the producer changes, never the page.
///
/// Serialized with a camelCase naming policy (see Program.cs AddSignalR / the contract test), so
/// fields arrive at the client as <c>runId</c>, <c>simTimeUtc</c>, etc.
/// </summary>
public sealed record RunProgress(
    string RunId,
    string Status,                       // running | completed | failed

    DateTime? SimTimeUtc,                // THE clock that advances ("moving date to date")

    int BarsProcessed,
    int BarsTotal,
    double Percent,
    double? EtaSeconds,

    long WallElapsedMs,
    double BarsPerSec,

    decimal Equity,
    decimal Balance,
    int OpenPositions,

    decimal DailyDdPct,
    decimal MaxDdPct,
    decimal DistanceToDailyLimit,

    string? GovernorState,
    string? GovernorReason,

    RunCounters Counters,

    // iter-strategy-system P3: which multi-pass combination is running (e.g. "EURUSD/H1", 2 of 5).
    string? CurrentPass = null,
    int PassIndex = 0,
    int PassTotal = 0);

/// <summary>The live funnel — signals → orders → fills → closes, plus rejections and breaches.</summary>
public sealed record RunCounters(
    int Signals,
    int Orders,
    int Fills,
    int Closes,
    int Rejections,
    int Breaches);

using TradingEngine.CTraderRunner;

namespace TradingEngine.Web.Services;

/// <summary>
/// The pluggable venue seam. A venue runner owns one venue's complete backtest execution —
/// engine-host construction, adapter wiring, data/transport plumbing and venue-specific
/// teardown — and reports progress/warnings onto the shared <see cref="BacktestRunState"/>.
/// The orchestrator owns everything venue-agnostic (queueing, lifecycle, finalize, records);
/// adding a venue is a new runner + DI registration, not an orchestrator edit.
/// </summary>
public interface IVenueRunner
{
    /// <summary>The venue ids this runner serves (matched case-insensitively against the run's
    /// "Venue" custom param by <see cref="VenueRunnerRegistry"/>).</summary>
    IReadOnlyList<string> VenueIds { get; }

    /// <summary>The run-log line announcing this venue's execution (kept byte-identical to the
    /// pre-refactor orchestrator log lines).</summary>
    string StartLogLine { get; }

    /// <summary>Execute the run's engine leg on this venue. Must not finalize the run — the
    /// orchestrator runs the shared finalize (barrier → stats → warnings → end record) after this
    /// returns. Cancellation/timeouts propagate as OperationCanceledException.</summary>
    Task<BacktestResult> ExecuteAsync(string runId, BacktestConfig cfg, BacktestRunState state, CancellationToken ct);
}

/// <summary>
/// iter-38 D6 / P0-B1: venue routing rules. cTrader is EXPLICIT opt-in (<c>"ctrader"</c>); the
/// default (no/empty selection) and any unknown value route to the credential-free replay venue.
/// Pure + config-free so venue routing is deterministically testable.
/// </summary>
public static class VenueRouting
{
    public static bool ResolveUseCtrader(string? venue) => venue?.ToLowerInvariant() switch
    {
        "ctrader" => true,
        "replay" or "sim" or "simulated" => false,
        _ => false,
    };
}

/// <summary>Resolves the runner for a run's "Venue" selection. Unknown/empty venues fall back to
/// the replay runner — the same routing <see cref="VenueRouting.ResolveUseCtrader"/> encodes.</summary>
public sealed class VenueRunnerRegistry
{
    private readonly Dictionary<string, IVenueRunner> _byId;
    private readonly IVenueRunner _default;

    public VenueRunnerRegistry(IEnumerable<IVenueRunner> runners)
    {
        _byId = new Dictionary<string, IVenueRunner>(StringComparer.OrdinalIgnoreCase);
        foreach (var runner in runners)
        {
            foreach (var id in runner.VenueIds)
            {
                _byId[id] = runner;
            }
        }

        _default = _byId.TryGetValue("replay", out var replay)
            ? replay
            : throw new InvalidOperationException("No venue runner registered for the default 'replay' venue.");
    }

    public IVenueRunner Resolve(string? venue) =>
        venue is { Length: > 0 } && _byId.TryGetValue(venue, out var runner) ? runner : _default;
}

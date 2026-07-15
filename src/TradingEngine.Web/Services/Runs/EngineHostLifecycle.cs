using TradingEngine.Domain;
using TradingEngine.Domain.Interfaces;
using TradingEngine.Infrastructure.Caching;
using TradingEngine.Infrastructure.Events;
using TradingEngine.Infrastructure.Persistence;

namespace TradingEngine.Web.Services;

/// <summary>
/// Shared per-run engine-host plumbing used by every venue runner: live equity polling into the
/// run state, final-equity capture, persistence flush before teardown, and async host disposal.
/// Extracted verbatim from BacktestOrchestrator.
/// </summary>
public static class EngineHostLifecycle
{
    // Live equity/DD for the Monitor. Reads the engine's in-memory AccountSnapshotStore (which moves
    // as the run progresses), NOT IBrokerAdapter.GetAccountStateAsync — the replay adapter's
    // GetAccountStateAsync returns the INITIAL balance forever, which left the Monitor's equity/DD
    // frozen and the page feeling "stuck".
    public static async Task StartEquityPollingAsync(
        IHost innerHost, BacktestRunState state, string runId, CancellationToken ct)
    {
        var store = innerHost.Services.GetService<IAccountSnapshotStore>();
        if (store is null) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(500, ct);
                ApplySnapshot(state, await store.GetByRunIdAsync(runId, ct));
            }
        }
        catch (OperationCanceledException) { }
    }

    // Capture the final snapshot onto the run state BEFORE the inner host (and its in-memory store)
    // is disposed, so the terminal RunProgress frame and the saved run summary show real equity/DD.
    public static void CaptureFinalEquity(BacktestRunState state, IHost innerHost, string runId)
    {
        var store = innerHost.Services.GetService<IAccountSnapshotStore>();
        if (store is null) return;
        try { ApplySnapshot(state, store.GetByRunIdAsync(runId, CancellationToken.None).GetAwaiter().GetResult()); }
        catch { /* best effort */ }
    }

    // Drain the buffered DB writers while the inner host's provider is still fully alive, so a short
    // run's equity curve and the tail of its bars are persisted (otherwise the 5s equity flush window
    // and the 500-bar batch threshold drop the run's data — the empty equity chart / "no bars" bugs).
    public static async Task FlushRunPersistenceAsync(IHost host)
    {
        try { await host.Services.GetRequiredService<EquityPersistenceHandler>().FlushAsync(); }
        catch { /* best effort */ }
        try { await host.Services.GetRequiredService<BufferedBarWriter>().FlushAsync(); }
        catch { /* best effort */ }
        // iter-36 K5: the StepRecord journal drains via ChannelJournalWriter.FlushAsync on engine dispose
        // (Wait-mode, lossless) — the old PipelineEventWriter/BarEvaluationHandler force-drains are gone.
    }

    public static async Task DisposeHostAsync(IHost host)
    {
        // Several engine singletons (BufferedBarWriter, EquityPersistenceHandler) are IAsyncDisposable;
        // sync Dispose() can't run their async teardown. Dispose the host asynchronously.
        if (host is IAsyncDisposable ad) await ad.DisposeAsync();
        else host.Dispose();
    }

    private static void ApplySnapshot(BacktestRunState state, IReadOnlyList<AccountSnapshot> snaps)
    {
        if (snaps.Count == 0) return;
        var latest = snaps[^1];
        state.Equity = latest.Equity;
        state.Balance = latest.Balance;
        state.HasEquityObservation = true;
        state.DailyDdPct = latest.DailyDrawdown;
        state.MaxDdPct = latest.MaxDrawdown;
        state.OpenPositions = latest.OpenPositions;
        // iter-38 W-A7: the governor band/reason + distance-to-daily-limit are sourced from the
        // authoritative kernel EngineState (via KernelEquitySnapshot.From), so the Monitor no longer
        // shows a blank governor.
        state.GovernorState = latest.GovernorState;
        state.GovernorReason = latest.GovernorReason;
        state.DistanceToDailyLimit = latest.DistanceToDailyLimit;
    }
}

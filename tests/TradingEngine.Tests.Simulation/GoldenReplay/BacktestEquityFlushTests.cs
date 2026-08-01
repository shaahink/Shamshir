using TradingEngine.Host;

namespace TradingEngine.Tests.Simulation.GoldenReplay;

/// <summary>
/// iter-37 Phase E (K-GAP-2) — proves the on-completion backtest equity flush: the in-memory buffered
/// snapshots are written to the EquitySnapshots table in one batch, carrying the run's real EngineMode
/// (the old PersistentEquitySink hard-coded Live), so GET /api/runs/{id}/equity is non-empty for a
/// finished backtest. The per-bar authoritative mapping itself is pinned by KernelEquitySnapshotTests.
/// </summary>
[Trait("Category", "KernelAcceptance")]
[Trait("Speed", "Fast")]
public sealed class BacktestEquityFlushTests
{
    private static AccountSnapshot Snap(int min, decimal equity, decimal maxDd) => new(
        new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(min),
        Balance: 10_000m, Equity: equity, FloatingPnL: equity - 10_000m,
        PeakEquity: 10_000m, DailyStartEquity: 10_000m, DailyDrawdown: maxDd, MaxDrawdown: maxDd,
        OpenPositions: 0, RunId: "run-e");

    [Fact]
    public void ToEquity_MapsAuthoritativeFields_WithRunMode()
    {
        var e = EquitySnapshotFlush.ToEquity(Snap(2, 9_500m, 0.05m), EngineMode.Backtest);

        e.Equity.Should().Be(9_500m);
        e.Balance.Should().Be(10_000m);
        e.CurrentMaxDrawdown.Should().Be(0.05m);
        e.Mode.Should().Be(EngineMode.Backtest, "the flush carries the run's mode, not a hard-coded Live");
    }

    [Fact]
    public async Task Flush_WritesAllBufferedSnapshots_AsOneBatch()
    {
        var sink = new BufferedEquitySink();
        sink.Observe(Snap(0, 10_000m, 0m));
        sink.Observe(Snap(1, 9_800m, 0.02m));
        sink.Observe(Snap(2, 9_500m, 0.05m));

        var repo = new FakeEquityRepository();
        await EquitySnapshotFlush.FlushAsync(sink.GetSnapshots(), repo, EngineMode.Backtest, "run-e", CancellationToken.None);

        repo.BatchCalls.Should().Be(1, "one batched write, not per-bar");
        repo.Saved.Should().HaveCount(3);
        repo.RunId.Should().Be("run-e");
        repo.Saved.Select(s => s.Equity).Should().Equal(10_000m, 9_800m, 9_500m);
        repo.Saved.Should().OnlyContain(s => s.Mode == EngineMode.Backtest);
    }

    private sealed class FakeEquityRepository : IEquityRepository
    {
        public int BatchCalls { get; private set; }
        public string? RunId { get; private set; }
        public List<EquitySnapshot> Saved { get; } = [];

        public Task SaveBatchAsync(IReadOnlyList<EquitySnapshot> snapshots, string? runId, CancellationToken ct)
        {
            BatchCalls++;
            RunId = runId;
            Saved.AddRange(snapshots);
            return Task.CompletedTask;
        }

        public Task SaveAsync(EquitySnapshot snapshot, string? runId, CancellationToken ct) { Saved.Add(snapshot); return Task.CompletedTask; }
        public Task<IReadOnlyList<EquitySnapshot>> GetByDateRangeAsync(DateTime from, DateTime to, CancellationToken ct) => Task.FromResult<IReadOnlyList<EquitySnapshot>>(Saved);
        public Task<IReadOnlyList<EquitySnapshot>> GetByRunIdAsync(string runId, CancellationToken ct) => Task.FromResult<IReadOnlyList<EquitySnapshot>>(Saved);
        public Task<EquitySnapshot?> GetLatestAsync(CancellationToken ct) => Task.FromResult(Saved.LastOrDefault());
    }
}

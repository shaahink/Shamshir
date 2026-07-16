using TradingEngine.Tests.Integration.Support;
using TradingEngine.Web.Services;

namespace TradingEngine.Tests.Integration.Experiments;

// iter-structural-edge S0: pins the committed F64 split-half machinery (SplitHalfPersistenceService,
// served as `research persistence`) on a synthetic experiment where every number is hand-checkable.
// The live-DB reproduction of the real F64 table (±$1) is the G0 gate, run against the app.
[Trait("Category", "Infrastructure")]
public sealed class SplitHalfPersistenceTests : IDisposable
{
    private readonly SqliteInMemory _db = new();
    private readonly Guid _experimentId = Guid.NewGuid();

    private static readonly DateOnly Split = new(2026, 2, 1);

    private void Seed()
    {
        using var ctx = _db.NewContext();
        ctx.Experiments.Add(new ExperimentEntity
        {
            Id = _experimentId,
            Name = "split-half-seed",
            Hypothesis = "",
            SpecJson = "{}",
            Status = "Completed",
            CreatedUtc = DateTime.UtcNow,
        });

        void Cell(string runId, string label, string scoreJson, params (DateTime closed, decimal pnl)[] trades)
        {
            ctx.BacktestRuns.Add(new BacktestRunEntity
            {
                RunId = runId,
                StartedAtUtc = DateTime.UtcNow,
                Status = "completed",
                Venue = "tape",
                BacktestFrom = new DateTime(2026, 1, 1),
                BacktestTo = new DateTime(2026, 3, 1),
                Symbol = "EURUSD",
                Period = "H1",
            });
            ctx.ExperimentRuns.Add(new ExperimentRunEntity
            {
                Id = Guid.NewGuid(),
                ExperimentId = _experimentId,
                BacktestRunId = runId,
                VariantLabel = label,
                ScoreJson = scoreJson,
            });
            foreach (var (closed, pnl) in trades)
            {
                ctx.Trades.Add(new TradeResultEntity
                {
                    Id = Guid.NewGuid(),
                    RunId = runId,
                    StrategyId = label,
                    Symbol = "EURUSD",
                    Direction = "Buy",
                    NetPnLAmount = pnl,
                    OpenedAtUtc = closed.AddHours(-2),
                    ClosedAtUtc = closed,
                });
            }
        }

        // H1 = before 2026-02-01, H2 = on/after. Hand math:
        //   A: H1 +100, H2 +50   (selected, persists)
        //   B: H1 +200, H2 -60   (selected, does not persist)
        //   C: H1 -10,  H2 +80   (not selected; H2-positive for the reverse check)
        //   D: null Composite    (must be excluded from the pool entirely)
        Cell("run-a", "cell-a", """{"Composite":50.0}""",
            (new DateTime(2026, 1, 10, 12, 0, 0), 100m), (new DateTime(2026, 2, 10, 12, 0, 0), 50m));
        Cell("run-b", "cell-b", """{"Composite":60.0}""",
            (new DateTime(2026, 1, 15, 12, 0, 0), 200m), (new DateTime(2026, 2, 15, 12, 0, 0), -60m));
        Cell("run-c", "cell-c", """{"Composite":40.0}""",
            (new DateTime(2026, 1, 20, 12, 0, 0), -10m), (new DateTime(2026, 2, 20, 12, 0, 0), 80m));
        Cell("run-d", "cell-d", """{"Composite":null,"NullReason":"below floor"}""",
            (new DateTime(2026, 1, 25, 12, 0, 0), 999m));

        ctx.SaveChanges();
    }

    [Fact]
    public async Task ComputesSelectionPersistence_FromHandCheckableSeed()
    {
        Seed();
        using var ctx = _db.NewContext();
        var svc = new SplitHalfPersistenceService(ctx);

        // Resolve by GUID prefix, exactly how the CLI passes 075D5240 for the census.
        var prefix = _experimentId.ToString("D")[..8];
        var report = await svc.ComputeAsync(prefix, Split, 100_000, CancellationToken.None);

        report.Error.Should().BeNull();
        report.ExperimentName.Should().Be("split-half-seed");
        report.ScoredCells.Should().Be(3, "the null-Composite cell is not part of the scored pool");
        report.H1PositiveCells.Should().Be(2);
        report.H1PnlOfSelection.Should().Be(300);
        report.H2PnlOfSelection.Should().Be(-10);
        report.PersistedCells.Should().Be(1);
        report.Top8.Select(c => c.VariantLabel).Should().ContainInOrder("cell-b", "cell-a");
        report.Top8H1Pnl.Should().Be(300);
        report.Top8H2Pnl.Should().Be(-10);
        report.H2PositiveCells.Should().Be(2, "cell-a and cell-c are H2-positive");
        report.ReverseH2Pnl.Should().Be(130);
        report.ReverseH1Pnl.Should().Be(90);
        report.H2Days.Should().Be(28, "2026-02-01 -> 2026-03-01");

        // Only two H2 trade dates and both windows would run past the last date -> no window
        // ever completes; the challenge table exists but is all zeroes at every scale.
        report.ChallengeWindows.Should().HaveCount(3);
        report.ChallengeWindows.Should().OnlyContain(w => w.Pass == 0 && w.Fail == 0 && w.Incomplete == 0);

        report.Text.Should().Contain("cells positive in H1: 2/3");
        report.Text.Should().Contain("persistence: 1/2");
    }

    [Fact]
    public async Task UnknownExperiment_ReturnsError_NotThrow()
    {
        Seed();
        using var ctx = _db.NewContext();
        var svc = new SplitHalfPersistenceService(ctx);

        var report = await svc.ComputeAsync("ffffffff", Split, 100_000, CancellationToken.None);

        report.Error.Should().Contain("no experiment matches");
    }

    public void Dispose() => _db.Dispose();
}

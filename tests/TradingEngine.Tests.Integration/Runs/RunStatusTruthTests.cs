using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Tests.Integration.Support;

namespace TradingEngine.Tests.Integration.Runs;

// P0.2 (F5, Q5) — run-status truth through the REAL persistence path (SQLite). The audited F5 bug:
// every cTrader run saved `failed` (ErrorMessage='disposed NetMQPoller') despite complete stats. The fix
// separates engine-result from teardown: a complete result + a teardown warning must round-trip as
// `completed-with-warnings` with ErrorMessage NULL — never `failed`. This test would have caught F5.
[Trait("Category", "Infrastructure")]
public sealed class RunStatusTruthTests : IDisposable
{
    private readonly SqliteInMemory _db = new();

    private BacktestRunSummary Base(string runId) => new(
        runId, DateTime.UtcNow.AddMinutes(-5), DateTime.MinValue,
        "EURUSD", "H1", "[\"EURUSD\"]", "[\"H1\"]", DateTime.UtcNow.AddDays(-7), DateTime.UtcNow,
        100_000m, "algo", "{}", "{}",
        0, 0, 0, 0, 0, 0, 0, 0, -1, null);

    private async Task<BacktestRunSummary> RoundTrip(BacktestRunSummary end)
    {
        using (var ctx = _db.NewContext())
        {
            var repo = new SqliteBacktestRunRepository(ctx);
            await repo.SaveAsync(Base(end.RunId), CancellationToken.None);
            await repo.UpdateAsync(end, CancellationToken.None);
        }
        using (var ctx = _db.NewContext())
        {
            var repo = new SqliteBacktestRunRepository(ctx);
            return (await repo.GetByIdAsync(end.RunId, CancellationToken.None))!;
        }
    }

    private static string Status(BacktestRunSummary s) => RunStatusResolver.Resolve(
        isCompleted: s.CompletedAtUtc != default, errorMessage: s.ErrorMessage, warningsJson: s.WarningsJson);

    [Fact]
    public async Task CompleteRun_WithTeardownWarning_RoundTrips_As_CompletedWithWarnings()
    {
        var warnings = """[{"code":"HOST_DISPOSE","detail":"Cannot access a disposed object. Object name: 'NetMQPoller'","atUtc":"2026-07-08T00:00:00Z"}]""";
        var end = Base("run-warn") with
        {
            CompletedAtUtc = DateTime.UtcNow,
            ExitCode = 0,
            ErrorMessage = null,
            WarningsJson = warnings,
            TotalTrades = 3,
            NetProfit = -389.51m,
        };

        var read = await RoundTrip(end);

        read.WarningsJson.Should().Be(warnings, "the teardown warning must persist for the UI/report");
        read.ErrorMessage.Should().BeNull("a teardown fault is NOT an engine-result failure");
        Status(read).Should().Be("completed-with-warnings");
    }

    [Fact]
    public async Task CompleteRun_NoWarnings_RoundTrips_As_Completed()
    {
        var end = Base("run-clean") with
        {
            CompletedAtUtc = DateTime.UtcNow, ExitCode = 0, ErrorMessage = null, WarningsJson = null,
            TotalTrades = 5,
        };

        var read = await RoundTrip(end);

        read.WarningsJson.Should().BeNull();
        Status(read).Should().Be("completed");
    }

    [Fact]
    public async Task RunWithNoResult_AndError_RoundTrips_As_Failed()
    {
        var end = Base("run-fail") with
        {
            CompletedAtUtc = DateTime.UtcNow, ExitCode = 1, ErrorMessage = "No bars found", WarningsJson = null,
        };

        var read = await RoundTrip(end);

        Status(read).Should().Be("failed");
    }

    public void Dispose() => _db.Dispose();
}

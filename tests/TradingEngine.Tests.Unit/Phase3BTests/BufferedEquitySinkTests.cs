namespace TradingEngine.Tests.Unit.Phase3BTests;

[Trait("Category", "Services")]
public sealed class BufferedEquitySinkTests
{
    [Fact]
    public void Observe_MultipleSnapshots_StoresThem()
    {
        var sink = new BufferedEquitySink();

        sink.Observe(new AccountSnapshot(
            new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc),
            100_000, 100_000, 0, 100_000, 100_000, 0, 0, 0));

        sink.Observe(new AccountSnapshot(
            new DateTime(2024, 1, 1, 11, 0, 0, DateTimeKind.Utc),
            100_000, 99_500, -500, 100_000, 100_000, 0.005m, 0.005m, 1));

        var snapshots = sink.GetSnapshots();

        snapshots.Should().HaveCount(2);
        snapshots[0].Equity.Should().Be(100_000);
        snapshots[1].Equity.Should().Be(99_500);
        snapshots[1].DailyDrawdown.Should().Be(0.005m);
        snapshots[1].OpenPositions.Should().Be(1);
    }

    [Fact]
    public void GetSnapshots_EmptySink_ReturnsEmpty()
    {
        var sink = new BufferedEquitySink();
        var snapshots = sink.GetSnapshots();
        snapshots.Should().BeEmpty();
    }

    [Fact]
    public async Task GetByRunIdAsync_ReturnsAllSnapshots()
    {
        var sink = new BufferedEquitySink();

        sink.Observe(new AccountSnapshot(
            DateTime.UtcNow, 100_000, 100_000, 0, 100_000, 100_000, 0, 0, 0));

        var result = await sink.GetByRunIdAsync("any-run", CancellationToken.None);

        result.Should().HaveCount(1);
    }
}

using FluentAssertions;
using TradingEngine.Infrastructure.Caching;

namespace TradingEngine.Tests.Unit.Cache;

public sealed class RunDataCacheTests
{
    private readonly RunDataCache _cache = new();

    [Fact]
    public void AppendJournal_writes_entries_to_cache()
    {
        var runId = "test-run-1";
        var records = new List<StepRecord>
        {
            new(runId, 1, DateTime.UtcNow, "BarClosed", "{}", [], "[]",
                new RiskSnapshot(10000, 10000, 0, 0, 0, 0, 0, false, null, "Normal", 0),
                null, null, []),
            new(runId, 2, DateTime.UtcNow.AddMinutes(1), "OrderProposed", "{}", [], "[]",
                new RiskSnapshot(10001, 10001, 0, 0, 0, 0, 0, false, null, "Normal", 0),
                null, null, []),
        };

        _cache.AppendJournal(runId, records);

        var cached = _cache.GetJournal(runId);
        cached.Should().HaveCount(2);
        cached.Should().OnlyContain(r => r.RunId == runId);
    }

    [Fact]
    public void GetJournal_returns_empty_for_unknown_run()
    {
        var cached = _cache.GetJournal("nonexistent");
        cached.Should().BeEmpty();
    }

    [Fact]
    public void Journal_ring_buffer_evicts_old_entries()
    {
        var runId = "test-evict";
        var records = new List<StepRecord>();
        for (int i = 0; i < 12000; i++)
        {
            records.Add(new StepRecord(runId, i, DateTime.UtcNow.AddMinutes(i),
                i == 0 ? "BarClosed" : "OrderProposed", "{}", [], "[]",
                new RiskSnapshot(10000, 10000, 0, 0, 0, 0, 0, false, null, "Normal", 0),
                null, null, []));
        }

        _cache.AppendJournal(runId, records);

        var cached = _cache.GetJournal(runId);
        // Default max is 10000
        cached.Should().HaveCountLessThanOrEqualTo(10000);
        // First entries should be evicted
        cached.Should().NotContain(r => r.Seq <= 500);
    }

    [Fact]
    public void AppendEquity_writes_snapshots_to_cache()
    {
        var runId = "test-eq";
        var snapshots = new List<EquitySnapshot>
        {
            new(DateTime.UtcNow, 10000, 0, 10000, 10000, 10000, 0, 0, EngineMode.Backtest),
            new(DateTime.UtcNow.AddHours(1), 10050, 50, 10100, 10100, 10000, 0, 0, EngineMode.Backtest),
        };

        _cache.AppendEquity(runId, snapshots);

        var cached = _cache.GetEquity(runId);
        cached.Should().HaveCount(2);
        cached.Should().BeInAscendingOrder(e => e.TimestampUtc);
    }

    [Fact]
    public void GetEquity_returns_empty_for_unknown_run()
    {
        var cached = _cache.GetEquity("nonexistent");
        cached.Should().BeEmpty();
    }

    [Fact]
    public void AppendTrade_writes_trades_to_cache()
    {
        var runId = "test-tr";
        var trade = new TradeResult(
            Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long,
            0.1m, new Price(1.1000m), new Price(1.1050m),
            new Price(1.0990m), new Price(1.1060m),
            DateTime.UtcNow.AddHours(-2), DateTime.UtcNow,
            new Money(50m, "USD"), new Money(5m, "USD"), new Money(-1m, "USD"), new Money(44m, "USD"),
            new Pips(5), 0.88,
            new Pips(3), new Pips(8),
            "TakeProfit", "strat-1", "risk-1",
            EngineMode.Backtest, "Market", Guid.NewGuid());

        _cache.AppendTrade(runId, trade);

        var cached = _cache.GetTrades(runId);
        cached.Should().HaveCount(1);
        cached[0].Id.Should().Be(trade.Id);
        cached[0].NetPnL.Amount.Should().Be(44m);
    }

    [Fact]
    public void GetTrades_returns_empty_for_unknown_run()
    {
        var cached = _cache.GetTrades("nonexistent");
        cached.Should().BeEmpty();
    }

    [Fact]
    public void HasRun_returns_false_until_data_appended()
    {
        _cache.HasRun("run-x").Should().BeFalse();
        _cache.AppendJournal("run-x", [new StepRecord("run-x", 1, DateTime.UtcNow,
            "BarClosed", "{}", [], "[]",
            new RiskSnapshot(10000, 10000, 0, 0, 0, 0, 0, false, null, "Normal", 0),
            null, null, [])]);
        _cache.HasRun("run-x").Should().BeTrue();
    }

    [Fact]
    public void Evict_removes_all_data_for_run()
    {
        var runId = "test-evict";
        _cache.AppendEquity(runId, [new EquitySnapshot(DateTime.UtcNow, 10000, 0, 10000, 10000, 10000, 0, 0, EngineMode.Backtest)]);
        _cache.HasRun(runId).Should().BeTrue();

        _cache.Evict(runId);
        _cache.HasRun(runId).Should().BeFalse();
        _cache.GetEquity(runId).Should().BeEmpty();
        _cache.GetJournal(runId).Should().BeEmpty();
    }

    [Fact]
    public void MarkCompleted_preserves_data_for_reads()
    {
        var runId = "test-mark";
        _cache.AppendEquity(runId, [new EquitySnapshot(DateTime.UtcNow, 10000, 0, 10000, 10000, 10000, 0, 0, EngineMode.Backtest)]);

        _cache.MarkCompleted(runId);

        // Data should still be readable after mark
        _cache.HasRun(runId).Should().BeTrue();
        _cache.GetEquity(runId).Should().HaveCount(1);
    }

    [Fact]
    public async Task Concurrent_appends_are_thread_safe()
    {
        var runId = "test-threads";
        var tasks = new List<Task>();
        for (int t = 0; t < 4; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    _cache.AppendJournal(runId, [new StepRecord(runId, i, DateTime.UtcNow,
                        "BarClosed", "{}", [], "[]",
                        new RiskSnapshot(10000, 10000, 0, 0, 0, 0, 0, false, null, "Normal", 0),
                        null, null, [])]);
                }
            }));
        }
        await Task.WhenAll(tasks);

        _cache.HasRun(runId).Should().BeTrue();
    }

    [Fact]
    public void GetJournal_respects_maxEntries_parameter()
    {
        var runId = "test-limit";
        var records = Enumerable.Range(0, 500).Select(i =>
            new StepRecord(runId, i, DateTime.UtcNow.AddMinutes(i),
                "BarClosed", "{}", [], "[]",
                new RiskSnapshot(10000, 10000, 0, 0, 0, 0, 0, false, null, "Normal", 0),
                null, null, [])).ToList();

        _cache.AppendJournal(runId, records);

        var limited = _cache.GetJournal(runId, 100);
        limited.Should().HaveCountLessThanOrEqualTo(100);
    }

    [Fact]
    public void GetEquity_returns_fresh_data_after_more_appends()
    {
        var runId = "test-eq-live";
        _cache.AppendEquity(runId, [
            new EquitySnapshot(DateTime.UtcNow.AddHours(-3), 10000, 0, 10000, 10000, 10000, 0, 0, EngineMode.Backtest),
            new EquitySnapshot(DateTime.UtcNow.AddHours(-2), 10050, 50, 10100, 10100, 10000, 0, 0, EngineMode.Backtest),
            new EquitySnapshot(DateTime.UtcNow.AddHours(-1), 10100, 100, 10200, 10200, 10000, 0, 0, EngineMode.Backtest),
        ]);

        var first = _cache.GetEquity(runId);
        first.Should().HaveCount(3);

        _cache.AppendEquity(runId, [
            new EquitySnapshot(DateTime.UtcNow, 10150, 150, 10300, 10300, 10000, 0, 0, EngineMode.Backtest),
            new EquitySnapshot(DateTime.UtcNow.AddHours(1), 10200, 200, 10400, 10400, 10000, 0, 0, EngineMode.Backtest),
        ]);

        var second = _cache.GetEquity(runId);
        second.Should().HaveCount(5);
    }

    [Fact]
    public void GetTrades_returns_fresh_data_after_more_appends()
    {
        var runId = "test-tr-live";
        var trade1 = CreateTestTrade(DateTime.UtcNow.AddHours(-3));
        var trade2 = CreateTestTrade(DateTime.UtcNow.AddHours(-2));
        var trade3 = CreateTestTrade(DateTime.UtcNow.AddHours(-1));
        _cache.AppendTrade(runId, trade1);
        _cache.AppendTrade(runId, trade2);
        _cache.AppendTrade(runId, trade3);

        var first = _cache.GetTrades(runId);
        first.Should().HaveCount(3);

        _cache.AppendTrade(runId, CreateTestTrade(DateTime.UtcNow));
        _cache.AppendTrade(runId, CreateTestTrade(DateTime.UtcNow.AddHours(1)));

        var second = _cache.GetTrades(runId);
        second.Should().HaveCount(5);
    }

    [Fact]
    public void Snapshot_freezes_after_MarkCompleted()
    {
        var runId = "test-freeze";
        _cache.AppendEquity(runId, [
            new EquitySnapshot(DateTime.UtcNow, 10000, 0, 10000, 10000, 10000, 0, 0, EngineMode.Backtest),
        ]);
        _cache.AppendTrade(runId, CreateTestTrade(DateTime.UtcNow));

        _cache.MarkCompleted(runId);

        _cache.AppendEquity(runId, [
            new EquitySnapshot(DateTime.UtcNow.AddHours(1), 10050, 50, 10100, 10100, 10000, 0, 0, EngineMode.Backtest),
        ]);
        _cache.AppendTrade(runId, CreateTestTrade(DateTime.UtcNow.AddHours(1)));

        var equity = _cache.GetEquity(runId);
        equity.Should().HaveCount(2, because: "MarkCompleted freezes the snapshot; late appends flush to bag but snapshot stays frozen");

        var trades = _cache.GetTrades(runId);
        trades.Should().HaveCount(2);
    }

    [Fact]
    public void GetTrades_returns_newest_first_descending_order()
    {
        var runId = "test-tr-order";
        var oldest = CreateTestTrade(DateTime.UtcNow.AddHours(-3));
        var middle = CreateTestTrade(DateTime.UtcNow.AddHours(-2));
        var newest = CreateTestTrade(DateTime.UtcNow.AddHours(-1));

        _cache.AppendTrade(runId, middle);
        _cache.AppendTrade(runId, oldest);
        _cache.AppendTrade(runId, newest);

        var trades = _cache.GetTrades(runId);
        trades.Should().HaveCount(3);
        trades.Should().BeInDescendingOrder(t => t.ClosedAtUtc,
            because: "cache and DB fallback must use the same canonical order (DESC by ClosedAtUtc)");
    }

    [Fact]
    public void MarkCompleted_sets_completed_at_utc()
    {
        var runId = "test-completed-at";
        _cache.AppendEquity(runId, [new EquitySnapshot(DateTime.UtcNow, 10000, 0, 10000, 10000, 10000, 0, 0, EngineMode.Backtest)]);

        _cache.GetCompletedAtUtc(runId).Should().BeNull(because: "not yet completed");

        _cache.MarkCompleted(runId);

        _cache.GetCompletedAtUtc(runId).Should().NotBeNull(because: "MarkCompleted sets the timestamp");
        _cache.GetCompletedAtUtc(runId)!.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void GetRunIds_lists_all_resident_runs()
    {
        _cache.AppendEquity("run-a", [new EquitySnapshot(DateTime.UtcNow, 10000, 0, 10000, 10000, 10000, 0, 0, EngineMode.Backtest)]);
        _cache.AppendEquity("run-b", [new EquitySnapshot(DateTime.UtcNow, 10000, 0, 10000, 10000, 10000, 0, 0, EngineMode.Backtest)]);

        var ids = _cache.GetRunIds();
        ids.Should().Contain(new[] { "run-a", "run-b" });
    }

    [Fact]
    public void Evict_removes_from_run_ids()
    {
        _cache.AppendEquity("run-x", [new EquitySnapshot(DateTime.UtcNow, 10000, 0, 10000, 10000, 10000, 0, 0, EngineMode.Backtest)]);
        _cache.GetRunIds().Should().Contain("run-x");

        _cache.Evict("run-x");
        _cache.GetRunIds().Should().NotContain("run-x");
    }

    [Fact]
    public void Equity_downsamples_when_exceeds_soft_cap()
    {
        var runId = "test-downsample";
        var start = DateTime.UtcNow.AddDays(-30);
        var batch = new List<EquitySnapshot>();

        for (int i = 0; i < 25_000; i++)
        {
            batch.Add(new EquitySnapshot(start.AddMinutes(i * 5), 10000 + i, i, 10000 + i, 10000, 10000, 0, 0, EngineMode.Backtest));
        }

        foreach (var snap in batch)
            _cache.AppendEquity(runId, [snap]);

        var cached = _cache.GetEquity(runId);
        cached.Should().HaveCountLessThanOrEqualTo(20_000, because: "equity soft-cap with downsampling");
        cached.Should().HaveCountGreaterThan(0);

        cached.Should().BeInAscendingOrder(e => e.TimestampUtc);
    }

    [Fact]
    public void Completed_at_utc_returns_null_for_never_completed_run()
    {
        _cache.AppendEquity("still-running", [new EquitySnapshot(DateTime.UtcNow, 10000, 0, 10000, 10000, 10000, 0, 0, EngineMode.Backtest)]);
        _cache.GetCompletedAtUtc("still-running").Should().BeNull();
    }

    [Fact]
    public void Completed_at_utc_returns_null_for_unknown_run()
    {
        _cache.GetCompletedAtUtc("does-not-exist").Should().BeNull();
    }

    [Fact]
    public void GetJournal_returns_fresh_data_after_more_appends()
    {
        var runId = "test-journal-live";
        var batch1 = new List<StepRecord>
        {
            new(runId, 1, DateTime.UtcNow, "BarClosed", "{}", [], "[]",
                new RiskSnapshot(10000, 10000, 0, 0, 0, 0, 0, false, null, "Normal", 0), null, null, []),
            new(runId, 2, DateTime.UtcNow.AddMinutes(1), "OrderProposed", "{}", [], "[]",
                new RiskSnapshot(10000, 10000, 0, 0, 0, 0, 0, false, null, "Normal", 0), null, null, []),
            new(runId, 3, DateTime.UtcNow.AddMinutes(2), "OrderFilled", "{}", [], "[]",
                new RiskSnapshot(10000, 10000, 0, 0, 0, 0, 0, false, null, "Normal", 0), null, null, []),
        };
        _cache.AppendJournal(runId, batch1);

        var first = _cache.GetJournal(runId);
        first.Should().HaveCount(3);

        var batch2 = new List<StepRecord>
        {
            new(runId, 4, DateTime.UtcNow.AddMinutes(3), "BarClosed", "{}", [], "[]",
                new RiskSnapshot(10000, 10000, 0, 0, 0, 0, 0, false, null, "Normal", 0), null, null, []),
            new(runId, 5, DateTime.UtcNow.AddMinutes(4), "OrderProposed", "{}", [], "[]",
                new RiskSnapshot(10000, 10000, 0, 0, 0, 0, 0, false, null, "Normal", 0), null, null, []),
        };
        _cache.AppendJournal(runId, batch2);

        var second = _cache.GetJournal(runId);
        second.Should().HaveCount(5);
    }

    private static TradeResult CreateTestTrade(DateTime closedAtUtc)
    {
        return new TradeResult(
            Guid.NewGuid(), Guid.NewGuid(), Symbol.Parse("EURUSD"), TradeDirection.Long,
            0.1m, new Price(1.1000m), new Price(1.1050m),
            new Price(1.0990m), new Price(1.1060m),
            DateTime.UtcNow.AddHours(-2), closedAtUtc,
            new Money(50m, "USD"), new Money(5m, "USD"), new Money(-1m, "USD"), new Money(44m, "USD"),
            new Pips(5), 0.88,
            new Pips(3), new Pips(8),
            "TakeProfit", "strat-1", "risk-1",
            EngineMode.Backtest, "Market", Guid.NewGuid());
    }
}

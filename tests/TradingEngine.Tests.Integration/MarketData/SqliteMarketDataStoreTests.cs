using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Tests.Integration.MarketData;

/// <summary>
/// iter-marketdata-tape P1 — canonical market-data store: write/dedupe, ordered range read, inventory, and
/// weekend-aware gap detection. Uses a temp-file SQLite via an <see cref="IDbContextFactory{T}"/> (in-memory
/// SQLite can't be shared across factory-created connections).
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class SqliteMarketDataStoreTests : IDisposable
{
    private readonly TempFactory _factory = new();
    private readonly SqliteMarketDataStore _store;
    private static readonly Symbol Eur = Symbol.Parse("EURUSD");

    public SqliteMarketDataStoreTests() => _store = new SqliteMarketDataStore(_factory);

    private static Bar H1(DateTime open, decimal close) =>
        new(Eur, Timeframe.H1, open, close, close + 0.0010m, close - 0.0010m, close, 100);

    private static Bar H1WithSpread(DateTime open, decimal close, decimal? spread) =>
        new(Eur, Timeframe.H1, open, close, close + 0.0010m, close - 0.0010m, close, 100, Spread: spread);

    [Fact]
    public async Task Write_then_read_returns_bars_in_open_time_order()
    {
        var t0 = new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc);
        // Intentionally out of order on the way in.
        await _store.WriteBarsAsync("ctrader", new[]
        {
            H1(t0.AddHours(2), 1.1030m),
            H1(t0, 1.1000m),
            H1(t0.AddHours(1), 1.1010m),
        }, default);

        var read = await _store.ReadBarsAsync(Eur, Timeframe.H1, t0, t0.AddHours(5), default);

        read.Select(b => b.OpenTimeUtc).Should().ContainInOrder(t0, t0.AddHours(1), t0.AddHours(2));
        read[0].Close.Should().Be(1.1000m);
    }

    // P6.2: per-bar spread stored and round-tripped correctly.
    [Fact]
    public async Task Write_then_read_preserves_bar_spread()
    {
        var t0 = new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc);
        await _store.WriteBarsAsync("ctrader", new[]
        {
            H1WithSpread(t0, 1.1000m, 0.00012m),
            H1WithSpread(t0.AddHours(1), 1.1010m, null),
        }, default);

        var read = await _store.ReadBarsAsync(Eur, Timeframe.H1, t0, t0.AddHours(5), default);

        read[0].Spread.Should().Be(0.00012m);
        read[1].Spread.Should().BeNull();
    }

    [Fact]
    public async Task Rewriting_the_same_window_is_idempotent()
    {
        var t0 = new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc);
        var bars = new[] { H1(t0, 1.1m), H1(t0.AddHours(1), 1.1m), H1(t0.AddHours(2), 1.1m) };

        var first = await _store.WriteBarsAsync("ctrader", bars, default);
        var second = await _store.WriteBarsAsync("ctrader", bars, default);
        // Overlapping-with-one-new: two dupes + one new.
        var third = await _store.WriteBarsAsync("ctrader", new[] { H1(t0.AddHours(2), 1.1m), H1(t0.AddHours(3), 1.1m) }, default);

        first.Should().Be(3);
        second.Should().Be(0, "the exact same window must insert nothing");
        third.Should().Be(1, "only the genuinely new bar is inserted");

        var all = await _store.ReadBarsAsync(Eur, Timeframe.H1, t0, t0.AddHours(9), default);
        all.Should().HaveCount(4);
    }

    [Fact]
    public async Task Inventory_reports_coverage_per_symbol_tf_source()
    {
        var t0 = new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc);
        await _store.WriteBarsAsync("ctrader", new[] { H1(t0, 1.1m), H1(t0.AddHours(1), 1.1m) }, default);

        var inv = await _store.GetInventoryAsync(default);

        inv.Should().ContainSingle();
        inv[0].Symbol.Should().Be("EURUSD");
        inv[0].Timeframe.Should().Be(Timeframe.H1);
        inv[0].Source.Should().Be("ctrader");
        inv[0].BarCount.Should().Be(2);
        inv[0].FirstOpenUtc.Should().Be(t0);
        inv[0].LastOpenUtc.Should().Be(t0.AddHours(1));
    }

    [Fact]
    public async Task Gaps_detects_a_midweek_hole()
    {
        // Wednesday: 10:00, 11:00, [12:00 missing], 13:00 → exactly one mid-week gap, NOT a weekend.
        var wed = new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc);
        await _store.WriteBarsAsync("ctrader", new[]
        {
            H1(wed, 1.1m), H1(wed.AddHours(1), 1.1m), H1(wed.AddHours(3), 1.1m),
        }, default);

        var gaps = await _store.GetGapsAsync(Eur, Timeframe.H1, wed, wed.AddHours(3), default);

        var gap = gaps.Should().ContainSingle().Subject;
        gap.StraddlesWeekend.Should().BeFalse();
        gap.AfterOpenUtc.Should().Be(wed.AddHours(1));
        gap.NextOpenUtc.Should().Be(wed.AddHours(3));
        gap.MissingBars.Should().Be(1);
    }

    [Fact]
    public async Task Gaps_flags_a_weekend_straddle()
    {
        // Friday 20:00 → Monday 00:00 with nothing between → one gap spanning Saturday.
        var fri = new DateTime(2024, 1, 5, 20, 0, 0, DateTimeKind.Utc);
        var mon = new DateTime(2024, 1, 8, 0, 0, 0, DateTimeKind.Utc);
        await _store.WriteBarsAsync("ctrader", new[] { H1(fri, 1.1m), H1(mon, 1.1m) }, default);

        var gaps = await _store.GetGapsAsync(Eur, Timeframe.H1, fri, mon, default);

        var gap = gaps.Should().ContainSingle().Subject;
        gap.StraddlesWeekend.Should().BeTrue();
        gap.AfterOpenUtc.Should().Be(fri);
        gap.NextOpenUtc.Should().Be(mon);
    }

    [Fact]
    public async Task DeleteBars_wholeRange_removesOnlyThatSymbolTimeframe()
    {
        var t0 = new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc);
        await _store.WriteBarsAsync("ctrader", new[] { H1(t0, 1.1m), H1(t0.AddHours(1), 1.1m) }, default);
        var h4 = new Bar(Eur, Timeframe.H4, t0, 1.1m, 1.11m, 1.09m, 1.1m, 100);
        await _store.WriteBarsAsync("ctrader", new[] { h4 }, default);

        var deleted = await _store.DeleteBarsAsync(Eur, Timeframe.H1, null, null, null, default);

        deleted.Should().Be(2);
        (await _store.ReadBarsAsync(Eur, Timeframe.H1, t0, t0.AddHours(5), default)).Should().BeEmpty();
        (await _store.ReadBarsAsync(Eur, Timeframe.H4, t0, t0.AddHours(5), default)).Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteBars_range_removesOnlyBarsInWindow()
    {
        var t0 = new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc);
        await _store.WriteBarsAsync("ctrader", new[]
        {
            H1(t0, 1.1m), H1(t0.AddHours(1), 1.1m), H1(t0.AddHours(2), 1.1m),
        }, default);

        var deleted = await _store.DeleteBarsAsync(Eur, Timeframe.H1, t0.AddHours(1), t0.AddHours(1), null, default);

        deleted.Should().Be(1);
        (await _store.ReadBarsAsync(Eur, Timeframe.H1, t0, t0.AddHours(5), default))
            .Select(b => b.OpenTimeUtc).Should().ContainInOrder(t0, t0.AddHours(2));
    }

    public void Dispose() => _factory.Dispose();

    private sealed class TempFactory : IDbContextFactory<MarketDataDbContext>, IDisposable
    {
        private readonly DbContextOptions<MarketDataDbContext> _opts;
        private readonly string _path;

        public TempFactory()
        {
            _path = Path.Combine(Path.GetTempPath(), $"mdtest-{Guid.NewGuid():N}.db");
            _opts = new DbContextOptionsBuilder<MarketDataDbContext>().UseSqlite($"Data Source={_path}").Options;
            using var db = new MarketDataDbContext(_opts);
            db.Database.EnsureCreated();
        }

        public MarketDataDbContext CreateDbContext() => new(_opts);

        public void Dispose()
        {
            try { File.Delete(_path); } catch { /* best effort */ }
        }
    }
}

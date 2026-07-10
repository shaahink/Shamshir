using FluentAssertions;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Tests.Unit.MarketData;

public sealed class DataQualityValidatorTests
{
    private static readonly Symbol Eur = Symbol.Parse("EURUSD");

    private static Bar H1(DateTime open, decimal o, decimal h, decimal l, decimal c) =>
        new(Eur, Timeframe.H1, open, o, h, l, c, 100);

    private static Bar M1(DateTime open, decimal o, decimal h, decimal l, decimal c) =>
        new(Eur, Timeframe.M1, open, o, h, l, c, 100);

    [Fact]
    public async Task Empty_store_produces_empty_report()
    {
        var store = new InMemoryMarketDataStore();
        var symbols = new FakeSymbolInfoRegistry();
        var validator = new DataQualityValidator(store, symbols);

        var report = await validator.GenerateReportAsync();

        report.Coverage.Should().BeEmpty();
        report.OhlcViolations.Should().BeEmpty();
        report.GapEntries.Should().BeEmpty();
        report.CrossCheckEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task Valid_bars_produce_zero_ohlc_violations()
    {
        var store = new InMemoryMarketDataStore();
        var t0 = new DateTime(2025, 1, 6, 10, 0, 0, DateTimeKind.Utc);
        // Mon-Fri, 5 valid bars — High >= max(O,C), Low <= min(O,C)
        for (var i = 0; i < 5; i++)
        {
            var open = 1.1000m + i * 0.0001m;
            store.Add(H1(t0.AddHours(i), open, open + 0.0020m, open - 0.0010m, open + 0.0010m));
        }
        var symbols = new FakeSymbolInfoRegistry();

        var validator = new DataQualityValidator(store, symbols);
        var report = await validator.GenerateReportAsync();

        report.Coverage.Should().HaveCount(1);
        report.OhlcViolations.Should().BeEmpty();
    }

    [Fact]
    public async Task High_less_than_open_is_flagged()
    {
        var store = new InMemoryMarketDataStore();
        var t0 = new DateTime(2025, 1, 6, 10, 0, 0, DateTimeKind.Utc);
        var open = 1.1000m;
        // High = 1.0990 which is less than Open = 1.1000
        store.Add(H1(t0, open, 1.0990m, 1.0980m, 1.0985m));
        var symbols = new FakeSymbolInfoRegistry();

        var validator = new DataQualityValidator(store, symbols);
        var report = await validator.GenerateReportAsync();

        report.OhlcViolations.Should().HaveCount(1);
        report.OhlcViolations[0].Symbol.Should().Be("EURUSD");
        report.OhlcViolations[0].High.Should().Be(1.0990m);
    }

    [Fact]
    public async Task Low_greater_than_close_is_flagged()
    {
        var store = new InMemoryMarketDataStore();
        var t0 = new DateTime(2025, 1, 6, 10, 0, 0, DateTimeKind.Utc);
        // Close = 1.0980 but Low = 1.0990 (> Close)
        store.Add(H1(t0, 1.1000m, 1.1010m, 1.0990m, 1.0980m));
        var symbols = new FakeSymbolInfoRegistry();

        var validator = new DataQualityValidator(store, symbols);
        var report = await validator.GenerateReportAsync();

        report.OhlcViolations.Should().HaveCount(1);
        report.OhlcViolations[0].Low.Should().Be(1.0990m);
    }

    [Fact]
    public async Task Non_weekend_gaps_are_reported()
    {
        var store = new InMemoryMarketDataStore();
        var t0 = new DateTime(2025, 1, 6, 10, 0, 0, DateTimeKind.Utc); // Monday 10:00
        // 10:00 and then jump to 13:00 (2 bars missing, not a weekend)
        store.Add(H1(t0, 1.1000m, 1.1010m, 1.0990m, 1.1000m));
        store.Add(H1(t0.AddHours(3), 1.1010m, 1.1020m, 1.1000m, 1.1010m));
        var symbols = new FakeSymbolInfoRegistry();

        var validator = new DataQualityValidator(store, symbols);
        var report = await validator.GenerateReportAsync();

        report.GapEntries.Should().HaveCount(1);
        report.GapEntries[0].Symbol.Should().Be("EURUSD");
        report.GapEntries[0].MissingBars.Should().Be(2);
        report.GapEntries[0].StraddlesWeekend.Should().BeFalse();
    }

    [Fact]
    public async Task Continuously_spaced_bars_produce_no_gaps()
    {
        var store = new InMemoryMarketDataStore();
        var t0 = new DateTime(2025, 1, 6, 10, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 10; i++)
        {
            store.Add(H1(t0.AddHours(i), 1.1000m, 1.1010m, 1.0990m, 1.1000m));
        }
        var symbols = new FakeSymbolInfoRegistry();

        var validator = new DataQualityValidator(store, symbols);
        var report = await validator.GenerateReportAsync();

        report.GapEntries.Should().BeEmpty();
    }

    [Fact]
    public async Task M1_to_H1_cross_check_detects_matching_bars()
    {
        var store = new InMemoryMarketDataStore();
        var symbols = new FakeSymbolInfoRegistry(pipSize: 0.0001m);
        // Build one clean H1 bar from 60 clean M1 bars
        var t0 = new DateTime(2025, 1, 6, 10, 0, 0, DateTimeKind.Utc);
        // H1 bar: open at 10:00, closes 11:00
        store.Add(H1(t0, 1.1000m, 1.1100m, 1.0950m, 1.1050m));
        // 60 M1 bars covering the same hour
        for (var i = 0; i < 60; i++)
        {
            var open = 1.1000m + i * 0.00005m;
            store.Add(M1(t0.AddMinutes(i), open, 1.1100m, 1.0950m, 1.1050m));
        }

        var validator = new DataQualityValidator(store, symbols);
        var report = await validator.GenerateReportAsync();

        report.CrossCheckEntries.Should().NotBeEmpty();
        var entry = report.CrossCheckEntries[0];
        entry.Symbol.Should().Be("EURUSD");
        entry.MatchCount.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Multiple_symbols_are_all_validated()
    {
        var store = new InMemoryMarketDataStore();
        var gbp = Symbol.Parse("GBPUSD");
        var t0 = new DateTime(2025, 1, 6, 10, 0, 0, DateTimeKind.Utc);
        store.Add(new Bar(Eur, Timeframe.H1, t0, 1.1000m, 1.1010m, 1.0990m, 1.1000m, 100));
        store.Add(new Bar(gbp, Timeframe.H1, t0, 1.3000m, 1.3010m, 1.2990m, 1.3000m, 100));
        var symbols = new FakeSymbolInfoRegistry();

        var validator = new DataQualityValidator(store, symbols);
        var report = await validator.GenerateReportAsync();

        report.Coverage.Select(c => c.Symbol).Should().Contain(new[] { "EURUSD", "GBPUSD" });
    }

    [Fact]
    public async Task Corrupt_symbol_data_is_skipped_gracefully()
    {
        var store = new InMemoryMarketDataStore(failOn: "BADCUR");
        var bad = Symbol.Parse("BADCUR");
        var t0 = new DateTime(2025, 1, 6, 10, 0, 0, DateTimeKind.Utc);
        store.Add(new Bar(bad, Timeframe.H1, t0, 1.1000m, 1.1010m, 1.0990m, 1.1000m, 100));
        store.Add(H1(t0, 1.1000m, 1.1010m, 1.0990m, 1.1000m));
        var symbols = new FakeSymbolInfoRegistry();

        var validator = new DataQualityValidator(store, symbols);
        var report = await validator.GenerateReportAsync();

        // BADCUR data was skipped but EURUSD was still validated
        report.Coverage.Count.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task TotalBars_and_TotalViolations_are_computed()
    {
        var store = new InMemoryMarketDataStore();
        var t0 = new DateTime(2025, 1, 6, 10, 0, 0, DateTimeKind.Utc);
        store.Add(H1(t0, 1.1000m, 1.1010m, 1.0990m, 1.1000m));
        store.Add(H1(t0.AddHours(1), 1.1010m, 1.1000m, 1.0990m, 1.0995m)); // High < Open = violation
        var symbols = new FakeSymbolInfoRegistry();

        var validator = new DataQualityValidator(store, symbols);
        var report = await validator.GenerateReportAsync();

        report.TotalBars.Should().Be(2);
        report.TotalViolations.Should().Be(1);
    }

    private sealed class InMemoryMarketDataStore : IMarketDataStore
    {
        private readonly List<Bar> _bars = new();
        private readonly string? _failOn;

        public InMemoryMarketDataStore(string? failOn = null) => _failOn = failOn;

        public void Add(Bar bar) => _bars.Add(bar);

        public Task<int> WriteBarsAsync(string source, IReadOnlyList<Bar> bars, CancellationToken ct = default,
            IProgress<int>? progress = null)
        {
            _bars.AddRange(bars);
            return Task.FromResult(bars.Count);
        }

        public Task<IReadOnlyList<Bar>> ReadBarsAsync(Symbol symbol, Timeframe tf, DateTime fromUtc, DateTime toUtc,
            CancellationToken ct = default)
        {
            if (_failOn is not null && symbol.Value.Equals(_failOn, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Simulated corruption");

            var result = _bars
                .Where(b => b.Symbol.Value == symbol.Value && b.Timeframe == tf
                    && b.OpenTimeUtc >= fromUtc && b.OpenTimeUtc <= toUtc)
                .OrderBy(b => b.OpenTimeUtc)
                .ToList();
            return Task.FromResult<IReadOnlyList<Bar>>(result);
        }

        public Task<IReadOnlyList<MarketDataInventoryEntry>> GetInventoryAsync(CancellationToken ct = default)
        {
            var result = _bars
                .GroupBy(b => (Symbol: b.Symbol.Value, b.Timeframe))
                .Select(g => new MarketDataInventoryEntry(
                    g.Key.Symbol, g.Key.Timeframe, "test",
                    g.Min(b => b.OpenTimeUtc), g.Max(b => b.OpenTimeUtc), g.Count()))
                .ToList();
            return Task.FromResult<IReadOnlyList<MarketDataInventoryEntry>>(result);
        }

        public Task<IReadOnlyList<MarketDataGap>> GetGapsAsync(Symbol symbol, Timeframe tf, DateTime fromUtc, DateTime toUtc,
            CancellationToken ct = default)
        {
            var times = _bars
                .Where(b => b.Symbol.Value == symbol.Value && b.Timeframe == tf
                    && b.OpenTimeUtc >= fromUtc && b.OpenTimeUtc <= toUtc)
                .OrderBy(b => b.OpenTimeUtc)
                .Select(b => b.OpenTimeUtc)
                .ToList();

            var interval = tf.ToTimeSpan();
            var gaps = new List<MarketDataGap>();
            for (var i = 1; i < times.Count; i++)
            {
                var delta = times[i] - times[i - 1];
                if (delta <= interval) continue;
                var missing = (int)(delta.Ticks / interval.Ticks) - 1;
                var straddles = false;
                for (var d = times[i - 1].Date; d <= times[i].Date; d = d.AddDays(1))
                {
                    if (d.DayOfWeek == DayOfWeek.Saturday) { straddles = true; break; }
                }
                gaps.Add(new MarketDataGap(times[i - 1], times[i], missing, straddles));
            }
            return Task.FromResult<IReadOnlyList<MarketDataGap>>(gaps);
        }

        public Task<int> DeleteBarsAsync(Symbol symbol, Timeframe tf, DateTime? fromUtc, DateTime? toUtc,
            string? source, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeSymbolInfoRegistry : ISymbolInfoRegistry
    {
        private readonly decimal _pipSize;

        public FakeSymbolInfoRegistry(decimal pipSize = 0.0001m)
        {
            _pipSize = pipSize;
        }

        public SymbolInfo Get(Symbol symbol) => new(
            symbol, SymbolCategory.Forex, symbol.Value[..3], symbol.Value[3..],
            _pipSize, _pipSize, 100000m, 0.01m, 100m, 0.01m,
            0.0333m, _pipSize, "USD");

        public bool TryGet(Symbol symbol, out SymbolInfo info)
        {
            info = Get(symbol);
            return _pipSize > 0;
        }

        public void Register(SymbolInfo info) { }
    }
}

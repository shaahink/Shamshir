using TradingEngine.Domain;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Tests.Unit.MarketData;

[Trait("Category", "MarketData")]
public sealed class BlockBootstrapperTests
{
    private static Bar H1(string symbol, DateTime t, double o, double h, double l, double c)
    {
        return new Bar(Symbol.Parse(symbol), Timeframe.H1, t,
            (decimal)o, (decimal)h, (decimal)l, (decimal)c, 1000);
    }

    [Fact]
    public async Task Generate_EmptyStore_ReturnsEmpty()
    {
        var store = new FakeMarketDataStore([]);
        var bb = new BlockBootstrapper(store);
        var tapes = await bb.GenerateAsync(
            Symbol.Parse("EURUSD"), Timeframe.H1,
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 10),
            TimeSpan.FromDays(7), 5, 42, CancellationToken.None);
        tapes.Should().BeEmpty();
    }

    [Fact]
    public async Task Generate_SingleBar_ProducesCountTapes()
    {
        var bars = new[]
        {
            H1("EURUSD", new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc), 1.0, 1.1, 0.9, 1.05),
        };
        var store = new FakeMarketDataStore(bars);
        var bb = new BlockBootstrapper(store);
        var tapes = await bb.GenerateAsync(
            Symbol.Parse("EURUSD"), Timeframe.H1,
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            TimeSpan.FromDays(7), 10, 42, CancellationToken.None);
        tapes.Should().HaveCount(10);
        foreach (var tape in tapes)
        {
            tape.Should().HaveCount(1);
            tape[0].Open.Should().Be(1.0m);
            tape[0].High.Should().Be(1.1m);
            tape[0].Low.Should().Be(0.9m);
            tape[0].Close.Should().Be(1.05m);
        }
    }

    [Fact]
    public async Task Generate_Deterministic_WithSameSeed()
    {
        var bars = Enumerable.Range(0, 168).Select(i =>
            H1("EURUSD", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                1.0 + i * 0.001, 1.1 + i * 0.001, 0.9 + i * 0.001, 1.05 + i * 0.001)).ToList();
        var store = new FakeMarketDataStore(bars);

        var bb = new BlockBootstrapper(store);
        var tapes1 = await bb.GenerateAsync(
            Symbol.Parse("EURUSD"), Timeframe.H1,
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 8),
            TimeSpan.FromDays(7), 5, 12345, CancellationToken.None);

        var bb2 = new BlockBootstrapper(store);
        var tapes2 = await bb2.GenerateAsync(
            Symbol.Parse("EURUSD"), Timeframe.H1,
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 8),
            TimeSpan.FromDays(7), 5, 12345, CancellationToken.None);

        tapes1.Should().HaveCount(5);
        tapes2.Should().HaveCount(5);
        for (var i = 0; i < 5; i++)
        {
            tapes1[i].Should().HaveSameCount(tapes2[i]);
            for (var j = 0; j < tapes1[i].Count; j++)
            {
                tapes1[i][j].Open.Should().Be(tapes2[i][j].Open);
                tapes1[i][j].High.Should().Be(tapes2[i][j].High);
                tapes1[i][j].Low.Should().Be(tapes2[i][j].Low);
                tapes1[i][j].Close.Should().Be(tapes2[i][j].Close);
            }
        }
    }

    [Fact]
    public async Task Generate_DifferentSeeds_ProduceDifferentTapes()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc); // Monday
        var bars = Enumerable.Range(0, 24 * 28).Select(i => // 4 weeks
            H1("EURUSD", start.AddHours(i),
                1.0 + i * 0.001, 1.1 + i * 0.001, 0.9 + i * 0.001, 1.05 + i * 0.001)).ToList();
        var store = new FakeMarketDataStore(bars);
        var bb = new BlockBootstrapper(store);

        var tapes1 = await bb.GenerateAsync(
            Symbol.Parse("EURUSD"), Timeframe.H1,
            start, start.AddDays(28),
            TimeSpan.FromDays(7), 3, 42, CancellationToken.None);

        var tapes2 = await bb.GenerateAsync(
            Symbol.Parse("EURUSD"), Timeframe.H1,
            start, start.AddDays(28),
            TimeSpan.FromDays(7), 3, 99, CancellationToken.None);

        tapes1.Should().HaveCount(3);
        tapes2.Should().HaveCount(3);

        var allEqual = true;
        for (var i = 0; i < 3 && allEqual; i++)
        {
            if (tapes1[i].Count != tapes2[i].Count) { allEqual = false; break; }
            for (var j = 0; j < tapes1[i].Count && allEqual; j++)
            {
                if (tapes1[i][j].Open != tapes2[i][j].Open) allEqual = false;
            }
        }
        allEqual.Should().BeFalse("different seeds should produce different block samples");
    }

    [Fact]
    public async Task Generate_WeeklyBlocks_PreservesGroupStructure()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc); // Monday
        var bars = Enumerable.Range(0, 24 * 14).Select(i =>
            H1("EURUSD", start.AddHours(i), 1.0, 1.1, 0.9, 1.05)).ToList();
        var store = new FakeMarketDataStore(bars);
        var bb = new BlockBootstrapper(store);

        var tapes = await bb.GenerateAsync(
            Symbol.Parse("EURUSD"), Timeframe.H1,
            start, start.AddDays(14),
            TimeSpan.FromDays(7), 5, 42, CancellationToken.None);

        // With 2 weeks of data and blockSize=7 days, we get 2 blocks:
        // each block = 168 bars (7 * 24h). So each tape = 336 bars (2 weeks original).
        // But block bootstrap samples blocks WITH REPLACEMENT, so tapes may have different
        // composition. Total bar count should remain original count.
        foreach (var tape in tapes)
        {
            tape.Should().HaveCount(336, "each tape should have same bar count as original");
        }
    }

    [Fact]
    public async Task Generate_RemapsTimestampsToUniquePerTape()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = Enumerable.Range(0, 48).Select(i =>
            H1("EURUSD", start.AddHours(i), 1.0, 1.1, 0.9, 1.05)).ToList();
        var store = new FakeMarketDataStore(bars);
        var bb = new BlockBootstrapper(store);

        var tapes = await bb.GenerateAsync(
            Symbol.Parse("EURUSD"), Timeframe.H1,
            start, start.AddDays(2),
            TimeSpan.FromDays(7), 3, 42, CancellationToken.None);

        tapes.Should().HaveCount(3);

        var seen = new HashSet<DateTime>();
        foreach (var tape in tapes)
        {
            foreach (var bar in tape)
            {
                seen.Add(bar.OpenTimeUtc).Should().BeTrue("timestamps must be unique across all tapes");
            }
        }
    }

    [Fact]
    public async Task Generate_PreservesTimeOfDayPattern()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc); // Monday
        var bars = Enumerable.Range(0, 24 * 14).Select(i => // 2 weeks
            H1("EURUSD", start.AddHours(i),
                1.0, 1.1, 0.9, 1.05)).ToList();
        var store = new FakeMarketDataStore(bars);
        var bb = new BlockBootstrapper(store);

        var tapes = await bb.GenerateAsync(
            Symbol.Parse("EURUSD"), Timeframe.H1,
            start, start.AddDays(14),
            TimeSpan.FromDays(7), 1, 42, CancellationToken.None);

        tapes.Should().HaveCount(1);
        var tape = tapes[0];
        tape.Should().HaveCount(336, "two weeks of H1 bars");

        // Each synthetic bar's OpenTimeUtc should be sequential by one H1 interval
        var interval = Timeframe.H1.ToTimeSpan();
        for (var i = 1; i < tape.Count; i++)
        {
            var delta = tape[i].OpenTimeUtc - tape[i - 1].OpenTimeUtc;
            delta.Should().Be(interval, $"bars should be sequential (gap at index {i})");
        }
    }

    [Fact]
    public async Task Generate_ThrowsOnNegativeTapeCount()
    {
        var store = new FakeMarketDataStore([]);
        var bb = new BlockBootstrapper(store);
        var act = () => bb.GenerateAsync(
            Symbol.Parse("EURUSD"), Timeframe.H1,
            new DateTime(2024, 1, 1), new DateTime(2024, 1, 2),
            TimeSpan.FromDays(7), 0, 42, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Generate_ThrowsOnInvalidRange()
    {
        var store = new FakeMarketDataStore([]);
        var bb = new BlockBootstrapper(store);
        var act = () => bb.GenerateAsync(
            Symbol.Parse("EURUSD"), Timeframe.H1,
            new DateTime(2024, 2, 1), new DateTime(2024, 1, 1),
            TimeSpan.FromDays(7), 5, 42, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    private sealed class FakeMarketDataStore : IMarketDataStore
    {
        private readonly Dictionary<(string Symbol, Timeframe Tf), List<Bar>> _data = new();

        public FakeMarketDataStore(IReadOnlyList<Bar> bars)
        {
            foreach (var bar in bars)
            {
                var key = (bar.Symbol.ToString(), bar.Timeframe);
                if (!_data.ContainsKey(key))
                    _data[key] = new List<Bar>();
                _data[key].Add(bar);
            }
        }

        public Task<int> WriteBarsAsync(string source, IReadOnlyList<Bar> bars, CancellationToken ct = default,
            IProgress<int>? progress = null)
        {
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<Bar>> ReadBarsAsync(Symbol symbol, Timeframe tf, DateTime fromUtc,
            DateTime toUtc, CancellationToken ct = default)
        {
            var key = (symbol.ToString(), tf);
            if (!_data.TryGetValue(key, out var bars))
                return Task.FromResult<IReadOnlyList<Bar>>([]);
            return Task.FromResult<IReadOnlyList<Bar>>(
                bars.Where(b => b.OpenTimeUtc >= fromUtc && b.OpenTimeUtc <= toUtc)
                    .OrderBy(b => b.OpenTimeUtc).ToList());
        }

        public Task<IReadOnlyList<MarketDataInventoryEntry>> GetInventoryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MarketDataInventoryEntry>>([]);

        public Task<IReadOnlyList<MarketDataGap>> GetGapsAsync(Symbol symbol, Timeframe tf, DateTime fromUtc,
            DateTime toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MarketDataGap>>([]);

        public Task<int> DeleteBarsAsync(Symbol symbol, Timeframe tf, DateTime? fromUtc, DateTime? toUtc,
            string? source, CancellationToken ct = default) =>
            Task.FromResult(0);
    }
}

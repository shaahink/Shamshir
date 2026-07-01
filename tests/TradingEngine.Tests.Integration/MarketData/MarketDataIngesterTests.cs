using FluentAssertions;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.MarketData;
using TradingEngine.Infrastructure.MarketData.Providers;

namespace TradingEngine.Tests.Integration.MarketData;

/// <summary>iter-marketdata-tape P2 — the ingester loads NDJSON shards into the canonical store (dedupe,
/// error-count) and the FileDrop provider proves the pluggable-source seam without cTrader.</summary>
[Trait("Category", "Infrastructure")]
public sealed class MarketDataIngesterTests : IDisposable
{
    private readonly TempMarketData _md = new();
    private static readonly Symbol Eur = Symbol.Parse("EURUSD");
    private static readonly DateTime T0 = new(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc);

    private static Bar H1(DateTime open) => new(Eur, Timeframe.H1, open, 1.1m, 1.1m, 1.1m, 1.1m, 10);

    [Fact]
    public async Task Ingests_shards_skips_junk_and_is_idempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shards-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "EURUSD_H1_202401.ndjson"), new[]
            {
                MarketDataShardIo.ToLine(H1(T0)),
                MarketDataShardIo.ToLine(H1(T0.AddHours(1))),
                "GARBAGE",
                "",
            });

            var first = await _md.Ingester.IngestDirectoryAsync(dir, "ctrader");
            first.BarsInserted.Should().Be(2);
            first.ParseErrors.Should().Be(1);
            first.LinesRead.Should().Be(3, "blank lines are skipped before counting");

            var second = await _md.Ingester.IngestDirectoryAsync(dir, "ctrader");
            second.BarsInserted.Should().Be(0, "re-ingest of the same shards inserts nothing");

            var stored = await _md.Store.ReadBarsAsync(Eur, Timeframe.H1, T0, T0.AddHours(5));
            stored.Should().HaveCount(2);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task FileDropProvider_ingests_from_its_drop_dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"drop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "x.ndjson"), new[] { MarketDataShardIo.ToLine(H1(T0)) });

            var provider = new FileDropProvider(_md.Ingester, dir);
            var res = await provider.DownloadAsync(
                new HistoricalDownloadRequest(new[] { "EURUSD" }, new[] { Timeframe.H1 }, T0, T0.AddDays(1)));

            res.Success.Should().BeTrue();
            res.BarsInserted.Should().Be(1);
            res.ShardFiles.Should().ContainSingle();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    public void Dispose() => _md.Dispose();
}

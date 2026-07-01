using FluentAssertions;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.MarketData;

namespace TradingEngine.Tests.Unit.MarketData;

/// <summary>iter-marketdata-tape P2 — the NDJSON interchange format round-trips a bar and rejects junk.</summary>
public sealed class MarketDataShardIoTests
{
    [Fact]
    public void Roundtrips_a_bar_preserving_utc_and_prices()
    {
        var bar = new Bar(Symbol.Parse("EURUSD"), Timeframe.H1,
            new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc),
            1.10005m, 1.10120m, 1.09980m, 1.10050m, 1234);

        var line = MarketDataShardIo.ToLine(bar);
        MarketDataShardIo.TryParse(line, out var back).Should().BeTrue();

        back.Symbol.Should().Be(bar.Symbol);
        back.Timeframe.Should().Be(Timeframe.H1);
        back.OpenTimeUtc.Should().Be(bar.OpenTimeUtc);
        back.OpenTimeUtc.Kind.Should().Be(DateTimeKind.Utc);
        ((double)back.Open).Should().BeApproximately(1.10005, 1e-9);
        ((double)back.Close).Should().BeApproximately(1.10050, 1e-9);
        back.Volume.Should().Be(1234);
    }

    [Fact]
    public void Parses_the_exact_cbot_recorder_line()
    {
        // The literal shape the recorder cBot writes: System.Text.Json camelCase, cTrader's lowercase
        // timeframe short-name ("h1"), UTC 'Z' time. Locks the wire contract we can't run cTrader to verify.
        const string line =
            "{\"symbol\":\"EURUSD\",\"timeframe\":\"h1\",\"openTimeUtc\":\"2024-01-03T10:00:00Z\"," +
            "\"open\":1.1,\"high\":1.101,\"low\":1.099,\"close\":1.1005,\"volume\":1234}";

        MarketDataShardIo.TryParse(line, out var bar).Should().BeTrue();
        bar.Symbol.Should().Be(Symbol.Parse("EURUSD"));
        bar.Timeframe.Should().Be(Timeframe.H1);
        bar.OpenTimeUtc.Should().Be(new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc));
        bar.OpenTimeUtc.Kind.Should().Be(DateTimeKind.Utc);
        ((double)bar.Close).Should().BeApproximately(1.1005, 1e-9);
        bar.Volume.Should().Be(1234);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"symbol\":\"EURUSD\"}")]                          // missing timeframe
    [InlineData("{\"symbol\":\"EURUSD\",\"timeframe\":\"ZZ99\"}")]   // unparseable timeframe
    public void Rejects_malformed_lines(string line)
    {
        MarketDataShardIo.TryParse(line, out _).Should().BeFalse();
    }
}

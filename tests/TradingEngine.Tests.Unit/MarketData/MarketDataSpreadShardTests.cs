namespace TradingEngine.Tests.Unit.MarketData;

using TradingEngine.Domain;
using TradingEngine.Infrastructure.MarketData;

[Trait("Category", "MarketData")]
[Trait("Speed", "Fast")]
public sealed class MarketDataSpreadShardTests
{
    [Fact]
    public void Roundtrips_bar_with_spread()
    {
        var bar = new Bar(Symbol.Parse("EURUSD"), Timeframe.H1,
            new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc),
            1.10005m, 1.10120m, 1.09980m, 1.10050m, 1234,
            Spread: 0.00012m);

        var line = MarketDataShardIo.ToLine(bar);
        line.Should().Contain("\"spread\"");

        MarketDataShardIo.TryParse(line, out var back).Should().BeTrue();
        back.Spread.Should().Be(0.00012m);
    }

    [Fact]
    public void Roundtrips_bar_without_spread()
    {
        var bar = new Bar(Symbol.Parse("EURUSD"), Timeframe.H1,
            new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc),
            1.10005m, 1.10120m, 1.09980m, 1.10050m, 1234);

        var line = MarketDataShardIo.ToLine(bar);
        MarketDataShardIo.TryParse(line, out var back).Should().BeTrue();
        back.Spread.Should().BeNull("null spread round-trips as null (serializer may emit 'spread':null)");
    }

    [Fact]
    public void Parses_cbot_line_with_spread()
    {
        const string line =
            "{\"symbol\":\"EURUSD\",\"timeframe\":\"h1\",\"openTimeUtc\":\"2024-01-03T10:00:00Z\"," +
            "\"open\":1.1,\"high\":1.101,\"low\":1.099,\"close\":1.1005,\"volume\":1234,\"spread\":0.00015}";

        MarketDataShardIo.TryParse(line, out var bar).Should().BeTrue();
        bar.Spread.Should().Be(0.00015m);
    }

    [Fact]
    public void Parses_cbot_line_without_spread_unchanged()
    {
        // Existing shards without "spread" key must still parse — backward compat.
        const string line =
            "{\"symbol\":\"EURUSD\",\"timeframe\":\"h1\",\"openTimeUtc\":\"2024-01-03T10:00:00Z\"," +
            "\"open\":1.1,\"high\":1.101,\"low\":1.099,\"close\":1.1005,\"volume\":1234}";

        MarketDataShardIo.TryParse(line, out var bar).Should().BeTrue();
        bar.Spread.Should().BeNull();
    }
}

using System.Text.Json;
using TradingEngine.Infrastructure;

namespace TradingEngine.Tests.Unit.Kernel;

[Trait("Category", "Kernel")]
[Trait("Speed", "Fast")]
public sealed class DatasetConfigHashTests
{
    [Fact]
    public void ConfigSetHash_SameJson_ProducesSameHash()
    {
        var json = """{"strategy":"mean-reversion","risk":0.01}""";
        var h1 = ConfigSetHash.Compute(json);
        var h2 = ConfigSetHash.Compute(json);
        h1.Should().Be(h2);
    }

    [Fact]
    public void ConfigSetHash_DifferentJson_ProducesDifferentHash()
    {
        var h1 = ConfigSetHash.Compute("""{"strategy":"mean-reversion","risk":0.01}""");
        var h2 = ConfigSetHash.Compute("""{"strategy":"mean-reversion","risk":0.02}""");
        h1.Should().NotBe(h2);
    }

    [Fact]
    public void ConfigSetHash_StableAcrossProcesses_Deterministic()
    {
        var json = """{"maxDailyLoss":0.05,"maxTotalLoss":0.10}""";
        var h1 = ConfigSetHash.Compute(json);
        var h2 = ConfigSetHash.Compute(json);
        h1.Should().Be(h2);
        h1.Length.Should().Be(64, "SHA256 hex produces 64 chars");
    }

    [Fact]
    public void ConfigSet_RoundTrip_JsonPreserved()
    {
        var original = new ConfigSet("cfg-1", "abc123", """{"strategy":"test"}""");
        var roundTripped = new ConfigSet(original.ConfigSetId, original.ContentHash, original.Json);

        roundTripped.ConfigSetId.Should().Be(original.ConfigSetId);
        roundTripped.ContentHash.Should().Be(original.ContentHash);
        roundTripped.Json.Should().Be(original.Json);
    }

    [Fact]
    public void ConfigSetHash_MatchesRoundTrip()
    {
        var json = """{"strategy":"mean-reversion","risk":0.01}""";
        var hash = ConfigSetHash.Compute(json);
        var config = new ConfigSet("cfg-2", hash, json);

        var recomputed = ConfigSetHash.Compute(config.Json);
        recomputed.Should().Be(config.ContentHash, "recomputing the hash over the stored JSON must match");
    }

    [Fact]
    public void DatasetRef_AllFieldsRoundTrip()
    {
        var original = new DatasetRef(
            "ds-1", "hash123", ["EURUSD"], ["H1"],
            new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            DatasetGranularity.Bar, 5000);

        var copy = new DatasetRef(
            original.DatasetId, original.ContentHash,
            original.Symbols, original.Timeframes,
            original.FromUtc, original.ToUtc,
            original.Granularity, original.RowCount);

        copy.Should().BeEquivalentTo(original);
    }
}

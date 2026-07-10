using TradingEngine.Services.ExitLab;

namespace TradingEngine.Tests.Unit.Phase45Tests;

/// <summary>
/// P4.5.2: ExcursionPathCodec unit tests — round-trip, edge cases, and malformed-rejection.
/// The codec serializes/parses <c>[{t,hi,lo},...]</c> (object format, not arrays) — one shared
/// implementation so the recorder and consumer can never format-mismatch again.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ExcursionPathCodecTests
{
    [Fact]
    public void RoundTrip_TwoPoints_PreservesAllFields()
    {
        var points = new List<ExcursionPoint>
        {
            new(0, 8.0, -4.0),
            new(60, -27.0, -52.0),
        };

        var json = ExcursionPathCodec.Serialize(points);
        var parsed = ExcursionPathCodec.Parse(json);

        parsed.Should().HaveCount(2);
        parsed[0].MinutesSinceEntry.Should().Be(0);
        parsed[0].HiPips.Should().BeApproximately(8.0, 0.01);
        parsed[0].LoPips.Should().BeApproximately(-4.0, 0.01);
        parsed[1].MinutesSinceEntry.Should().Be(60);
        parsed[1].HiPips.Should().BeApproximately(-27.0, 0.01);
        parsed[1].LoPips.Should().BeApproximately(-52.0, 0.01);
    }

    [Fact]
    public void RoundTrip_SinglePoint_PreservesSigns()
    {
        var points = new List<ExcursionPoint> { new(10, 2.5, -3.7) };

        var json = ExcursionPathCodec.Serialize(points);
        var parsed = ExcursionPathCodec.Parse(json);

        parsed.Should().ContainSingle();
        parsed[0].HiPips.Should().BeApproximately(2.5, 0.01);
        parsed[0].LoPips.Should().BeApproximately(-3.7, 0.01);
    }

    [Fact]
    public void Parse_Null_ReturnsEmpty()
    {
        ExcursionPathCodec.Parse(null!).Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        ExcursionPathCodec.Parse("").Should().BeEmpty();
    }

    [Fact]
    public void Parse_WhitespaceString_ReturnsEmpty()
    {
        ExcursionPathCodec.Parse("   ").Should().BeEmpty();
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        var act = () => ExcursionPathCodec.Parse("not-json-at-all");

        act.Should().Throw<System.Text.Json.JsonException>("malformed paths must surface, never vanish silently");
    }

    [Fact]
    public void Parse_ArrayOfArrays_Throws()
    {
        var act = () => ExcursionPathCodec.Parse("[[0,8.0,-4.0],[60,-27.0,-52.0]]");

        act.Should().Throw<System.Text.Json.JsonException>("array-of-arrays is the OLD format — codec expects objects with named fields");
    }

    [Fact]
    public void Serialize_EmptyList_ProducesEmptyJsonArray()
    {
        var json = ExcursionPathCodec.Serialize([]);
        json.Should().Be("[]");
    }

    [Fact]
    public void Serialize_ProducesObjectFormat_NotArrayOfArrays()
    {
        var points = new List<ExcursionPoint> { new(0, 1.5, -2.0) };
        var json = ExcursionPathCodec.Serialize(points);

        json.Should().Contain("\"t\"");
        json.Should().Contain("\"hi\"");
        json.Should().Contain("\"lo\"");
        json.Should().NotContain("[0,");
        json.Should().NotContain("[1.5,");
    }

    [Fact]
    public void RoundTrip_MatchesTapeReplayAdapterFormat()
    {
        // P4.5.2 gate: the codec's JSON must be byte-compatible with what TapeReplayAdapter
        // recorded before P4.5.2 (the object format). Parse a hand-crafted object-format JSON
        // — the exact shape the live DB already stores — and verify it round-trips.
        var jsonFromDb = """[{"t":0,"hi":8.0,"lo":-4.0},{"t":60,"hi":-27.0,"lo":-52.0}]""";

        var parsed = ExcursionPathCodec.Parse(jsonFromDb);

        parsed.Should().HaveCount(2);
        parsed[0].MinutesSinceEntry.Should().Be(0);
        parsed[0].HiPips.Should().BeApproximately(8.0, 0.01);
        parsed[0].LoPips.Should().BeApproximately(-4.0, 0.01);
        parsed[1].MinutesSinceEntry.Should().Be(60);
        parsed[1].HiPips.Should().BeApproximately(-27.0, 0.01);
        parsed[1].LoPips.Should().BeApproximately(-52.0, 0.01);

        // And re-serializing produces the same shape
        var reserialized = ExcursionPathCodec.Serialize(parsed);
        var reparsed = ExcursionPathCodec.Parse(reserialized);
        reparsed.Should().HaveCount(2);
    }

    [Fact]
    public void Serialize_RoundsHiLoToOneDecimal()
    {
        var points = new List<ExcursionPoint> { new(0, 8.123456789, -4.987654321) };

        var json = ExcursionPathCodec.Serialize(points);
        var parsed = ExcursionPathCodec.Parse(json);

        parsed.Should().ContainSingle();
        parsed[0].HiPips.Should().BeApproximately(8.1, 0.01);
        parsed[0].LoPips.Should().BeApproximately(-5.0, 0.01);
    }
}

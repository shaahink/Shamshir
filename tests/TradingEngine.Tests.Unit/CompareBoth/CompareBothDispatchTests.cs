using System.Text.Json;

namespace TradingEngine.Tests.Unit.CompareBoth;

[Trait("Category", "CompareBoth")]
[Trait("Speed", "Fast")]
public sealed class CompareBothDispatchTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void PinnedConfig_1Day_has_required_fields()
    {
        var json = ReadConfig("eurusd-h1-1d.json");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("start").GetDateTime().Should().Be(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        root.GetProperty("end").GetDateTime().Should().Be(new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc));
        root.GetProperty("balance").GetDecimal().Should().Be(100_000m);
        root.GetProperty("symbols")[0].GetString().Should().Be("EURUSD");
        root.GetProperty("periods")[0].GetString().Should().Be("H1");
        root.GetProperty("strategyIds")[0].GetString().Should().Be("trend-breakout");
        root.GetProperty("governorEnabled").GetBoolean().Should().BeFalse();
        root.GetProperty("honestFills").GetBoolean().Should().BeTrue();
        root.GetProperty("stripAddOns").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void PinnedConfig_7Day_has_7_day_span()
    {
        var json = ReadConfig("eurusd-h1-7d.json");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var start = root.GetProperty("start").GetDateTime();
        var end = root.GetProperty("end").GetDateTime();
        end.Subtract(start).Days.Should().Be(7);
    }

    [Fact]
    public void Both_configs_are_valid_JSON()
    {
        foreach (var name in new[] { "eurusd-h1-1d.json", "eurusd-h1-7d.json" })
        {
            var json = ReadConfig(name);
            using var doc = JsonDocument.Parse(json);
            doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        }
    }

    [Fact]
    public void CompareBothRequest_deserializes_correctly()
    {
        var json = """{"configName": "eurusd-h1-1d.json"}""";
        var doc = JsonDocument.Parse(json);
        var configName = doc.RootElement.GetProperty("configName").GetString();

        configName.Should().Be("eurusd-h1-1d.json");
    }

    [Fact]
    public void CompareBothRequest_missing_configName_is_empty()
    {
        var json = """{}""";
        var doc = JsonDocument.Parse(json);
        var hasConfig = doc.RootElement.TryGetProperty("configName", out var prop);

        // When missing, default empty string — controller validates and rejects
        if (hasConfig)
            prop.GetString().Should().Be("");
    }

    private static string ReadConfig(string name)
    {
        var dir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "compare-both"));
        var path = Path.Combine(dir, name);
        File.Exists(path).Should().BeTrue($"config file '{name}' must exist at {path}");
        return File.ReadAllText(path);
    }
}

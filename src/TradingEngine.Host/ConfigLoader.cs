using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingEngine.Host;

public sealed record LoadedConfig(
    IReadOnlyList<PropFirmRuleSet> PropFirms,
    IReadOnlyList<RiskProfile> RiskProfiles,
    IReadOnlyList<StrategyConfigEntry> StrategyConfigs)
{
    public IReadOnlyList<NewsBlockWindow> NewsWindows { get; init; } = [];
    public StrategyRotationOptions? StrategyRotation { get; init; }
}

public sealed record StrategyConfigEntry(
    string Id,
    string DisplayName,
    bool Enabled,
    IReadOnlyList<string> Symbols,
    string RiskProfileId,
    JsonElement Parameters,
    string Timeframe = "H1")
{
    public RegimeFilterOptions? RegimeFilter { get; init; }
    public OrderEntryOptions? OrderEntry { get; init; }
    public PositionManagementOptions? PositionManagement { get; init; }
}

public sealed class ConfigLoader
{
    private readonly string _basePath;

    public ConfigLoader(string? basePath = null)
    {
        _basePath = basePath ?? AppContext.BaseDirectory;
    }

    public LoadedConfig Load()
    {
        var propFirms = LoadDirectory<PropFirmRuleSet>("prop-firms");
        var riskProfiles = LoadDirectory<RiskProfile>("risk-profiles");
        var strategyConfigs = LoadStrategyConfigs();

        ValidateCrossReferences(riskProfiles, strategyConfigs, propFirms);

        var newsWindows = LoadNewsWindows();
        var rotation = LoadOptionalFile<StrategyRotationOptions>("rotation.json");

        return new LoadedConfig(propFirms, riskProfiles, strategyConfigs)
        {
            NewsWindows = newsWindows,
            StrategyRotation = rotation,
        };
    }

    private List<T> LoadDirectory<T>(string subDir)
    {
        var dir = Path.Combine(_basePath, "config", subDir);
        if (!Directory.Exists(dir))
            return [];

        var results = new List<T>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var json = File.ReadAllText(file);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
            };
            var item = JsonSerializer.Deserialize<T>(json, options);
            if (item is not null)
                results.Add(item);
        }
        return results;
    }

    private List<StrategyConfigEntry> LoadStrategyConfigs()
    {
        var dir = Path.Combine(_basePath, "config", "strategies");
        if (!Directory.Exists(dir))
            return [];

        var results = new List<StrategyConfigEntry>();
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var json = File.ReadAllText(file);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            };
            var doc = JsonSerializer.Deserialize<JsonDocument>(json, options);
            if (doc is null) continue;

            var root = doc.RootElement;
            var parameters = root.TryGetProperty("parameters", out var p) ? p.Clone() : default;
            var timeframe = root.TryGetProperty("timeframe", out var tf) ? tf.GetString()! : "H1";

            results.Add(new StrategyConfigEntry(
                root.GetProperty("id").GetString()!,
                root.GetProperty("displayName").GetString()!,
                root.TryGetProperty("enabled", out var en) && en.GetBoolean(),
                root.GetProperty("symbols").EnumerateArray().Select(s => s.GetString()!).ToList(),
                root.GetProperty("riskProfileId").GetString()!,
                parameters,
                timeframe)
            {
                RegimeFilter = ParseOptional<RegimeFilterOptions>(root, "regimeFilter"),
                OrderEntry = ParseOptional<OrderEntryOptions>(root, "orderEntry"),
                PositionManagement = ParseOptional<PositionManagementOptions>(root, "positionManagement"),
            });
        }
        return results;
    }

    private static void ValidateCrossReferences(
        IReadOnlyList<RiskProfile> riskProfiles,
        IReadOnlyList<StrategyConfigEntry> strategies,
        IReadOnlyList<PropFirmRuleSet> propFirms)
    {
        var profileIds = riskProfiles.Select(p => p.Id).ToHashSet();
        var firmIds = propFirms.Select(f => f.Id).ToHashSet();

        foreach (var strategy in strategies)
        {
            if (!profileIds.Contains(strategy.RiskProfileId))
            {
                throw new InvalidOperationException(
                    $"Strategy '{strategy.Id}' references riskProfileId '{strategy.RiskProfileId}' " +
                    $"which was not found in config/risk-profiles/. Available: [{string.Join(", ", profileIds)}]");
            }
        }

        foreach (var profile in riskProfiles)
        {
            if (!firmIds.Contains(profile.PropFirmRuleSetId))
            {
                throw new InvalidOperationException(
                    $"RiskProfile '{profile.Id}' references propFirmRuleSetId '{profile.PropFirmRuleSetId}' " +
                    $"which was not found in config/prop-firms/. Available: [{string.Join(", ", firmIds)}]");
            }
        }
    }

    private IReadOnlyList<NewsBlockWindow> LoadNewsWindows()
    {
        var path = Path.Combine(_basePath, "config", "news", "blocked-windows.json");
        if (!File.Exists(path)) return [];

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };
        var wrapper = JsonSerializer.Deserialize<NewsWindowsWrapper>(json, options);
        return wrapper?.Windows ?? [];
    }

    private T? LoadOptionalFile<T>(string fileName) where T : class
    {
        var path = Path.Combine(_basePath, "config", fileName);
        if (!File.Exists(path)) return null;

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };
        return JsonSerializer.Deserialize<T>(json, options);
    }

    private sealed record NewsWindowsWrapper(IReadOnlyList<NewsBlockWindow> Windows);

    private static T? ParseOptional<T>(JsonElement root, string propertyName) where T : class
    {
        if (!root.TryGetProperty(propertyName, out var elem)) return null;
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };
        return JsonSerializer.Deserialize<T>(elem.GetRawText(), opts);
    }
}

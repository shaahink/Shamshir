using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingEngine.Infrastructure.Configuration;

public sealed class ConfigLoader
{
    private readonly string _basePath;

    public ConfigLoader(string? basePath = null)
    {
        _basePath = basePath ?? AppContext.BaseDirectory;
    }

    public LoadedConfig Load()
    {
        var config = LoadBase();
        config.StrategyConfigs = LoadStrategyConfigs();

        ValidateCrossReferences(config.RiskProfiles, config.StrategyConfigs, config.PropFirms);

        return config;
    }

    public LoadedConfig LoadBase()
    {
        var propFirms = LoadDirectory<PropFirmRuleSet>("prop-firms");
        var riskProfiles = LoadDirectory<RiskProfile>("risk-profiles");

        var newsWindows = LoadNewsWindows();
        var rotation = LoadOptionalFile<StrategyRotationOptions>("rotation.json");
        var governor = LoadOptionalFile<GovernorOptions>("governor.json") ?? new GovernorOptions();
        var sizingPolicy = LoadOptionalFile<SizingPolicyOptions>("sizing-policy.json") ?? new SizingPolicyOptions();
        var regime = LoadOptionalFile<RegimeOptions>("regime.json") ?? new RegimeOptions();

        return new LoadedConfig(propFirms, riskProfiles)
        {
            NewsWindows = newsWindows,
            StrategyRotation = rotation,
            Governor = governor,
            SizingPolicy = sizingPolicy,
            Regime = regime,
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
                Reentry = ParseOptional<ReentryOptions>(root, "reentry"),
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

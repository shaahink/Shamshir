using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingEngine.Host;

public sealed record LoadedConfig(
    IReadOnlyList<PropFirmRuleSet> PropFirms,
    IReadOnlyList<RiskProfile> RiskProfiles,
    IReadOnlyList<StrategyConfigEntry> StrategyConfigs);

public sealed record StrategyConfigEntry(
    string Id,
    string DisplayName,
    bool Enabled,
    IReadOnlyList<string> Symbols,
    string RiskProfileId,
    JsonElement Parameters,
    string Timeframe = "H1");

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

        return new LoadedConfig(propFirms, riskProfiles, strategyConfigs);
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
                timeframe));
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
}

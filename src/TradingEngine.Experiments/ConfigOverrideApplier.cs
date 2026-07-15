using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TradingEngine.Experiments;

public static class ConfigOverrideApplier
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static LoadedConfig Apply(LoadedConfig config, Dictionary<string, JsonElement> overrides)
    {
        if (overrides.Count == 0)
            return config;

        var json = JsonSerializer.Serialize(config, SerializerOptions);
        var node = JsonNode.Parse(json)
            ?? throw new InvalidOperationException("Failed to parse LoadedConfig to JSON");

        foreach (var (path, value) in overrides)
        {
            var target = NavigateTo(node, path);
            var valueNode = JsonNode.Parse(value.GetRawText())
                ?? throw new InvalidOperationException($"Invalid override value for path '{path}'");
            target.ReplaceWith(valueNode);
        }

        var modified = JsonSerializer.Deserialize<LoadedConfig>(node.ToJsonString(), SerializerOptions);
        if (modified is null)
            throw new InvalidOperationException("Failed to deserialize modified LoadedConfig");

        return modified;
    }

    private static JsonNode NavigateTo(JsonNode root, string dottedPath)
    {
        var segments = dottedPath.Split('.');
        var current = root;

        for (var i = 0; i < segments.Length; i++)
        {
            current = TryFind(current, segments[i])
                ?? throw new InvalidOperationException(
                    $"Override path '{dottedPath}' not found: segment '{segments[i]}' does not exist");
        }

        return current;
    }

    private static JsonNode? TryFind(JsonNode parent, string segment)
    {
        if (parent is JsonObject obj)
        {
            var candidates = obj.Where(kv =>
                string.Equals(kv.Key, segment, StringComparison.OrdinalIgnoreCase)).ToList();

            if (candidates.Count == 1)
                return candidates[0].Value!;

            return null;
        }

        if (parent is JsonArray arr && int.TryParse(segment, out var index))
            return arr[index];

        return null;
    }
}

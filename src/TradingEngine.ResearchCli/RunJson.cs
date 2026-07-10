using System.Text.Json;

namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.1 — tolerant, pure parsing of the Web API's run-detail JSON (<c>GET /api/runs/{id}</c>) into the
/// facts the gate check needs. Kept separate from the HTTP shell so it is unit-tested against captured
/// payloads (the field names are camelCased by the app's STJ policy: status/totalTrades/warningsJson/
/// errorMessage). Unknown/missing fields degrade gracefully rather than throwing — an agent-facing tool
/// must never crash on an unexpected shape; it emits a FAIL verdict instead.
/// </summary>
public static class RunJson
{
    public static RunGateInput ParseRun(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new RunGateInput(
            Status: GetString(root, "status") ?? "unknown",
            TotalTrades: GetInt(root, "totalTrades"),
            WarningsJson: GetRawOrString(root, "warningsJson"),
            ErrorMessage: GetString(root, "errorMessage"));
    }

    private static string? GetString(JsonElement root, string name) =>
        TryGet(root, name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int GetInt(JsonElement root, string name)
    {
        if (TryGet(root, name, out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
            {
                return n;
            }
            if (el.ValueKind == JsonValueKind.String
                && int.TryParse(el.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var s))
            {
                return s;
            }
        }
        return 0;
    }

    // warningsJson can arrive as a JSON string OR as an embedded array/object; normalize to raw text.
    private static string? GetRawOrString(JsonElement root, string name)
    {
        if (!TryGet(root, name, out var el))
        {
            return null;
        }
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => el.GetRawText(),
        };
    }

    // Case-insensitive property lookup (STJ camelCase vs any producer drift).
    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}

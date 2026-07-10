using System.Text.Json;

namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.1 — tolerant, pure extraction of the machine facts the <c>exitlab eval</c> and <c>walkforward</c>
/// verbs turn into their <c>VERDICT:</c> line. Kept separate from the HTTP shell so the summary/verdict
/// mapping is unit-tested against captured payloads: exit-lab returns
/// <c>{ totalTrades, totalCells, cells:[…] }</c> and walk-forward's start returns <c>{ jobId, status }</c>.
/// Unknown/missing fields degrade to safe defaults — an agent-facing tool emits a FAIL verdict, never a crash.
/// </summary>
public static class ExitLabResult
{
    public static (int TotalTrades, int TotalCells) ParseSummary(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (GetInt(root, "totalTrades"), GetInt(root, "totalCells"));
    }

    public static string ParseJobId(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return GetString(doc.RootElement, "jobId") ?? "";
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

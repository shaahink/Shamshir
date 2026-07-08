using System.Text.Json;
using System.Text.Json.Nodes;

namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.1 — the pure body-builder behind <c>research run start --plan plan.json [--venue] [--compare-both]
/// [--explore]</c>. A plan file IS a <c>StartRunRequest</c> (the same JSON the UI POSTs to
/// <c>/api/runs</c>); this merges the file with CLI flag overrides so an agent can reuse one plan across
/// venues without editing it. Kept pure (JsonNode in → JSON string out) so the merge precedence is
/// unit-tested credential-free; the HTTP POST is a thin shell. Field names match
/// <c>StartRunRequest</c> (camelCase-tolerant server STJ): venue, compareBoth, explorationMode,
/// recordExcursions.
/// </summary>
public static class StartRunPlan
{
    /// <summary>
    /// Merge <paramref name="planJson"/> with the optional overrides and return the request body.
    /// Overrides win over the file. <paramref name="explore"/> sets BOTH explorationMode and
    /// recordExcursions (the one-click exploration preset — see StartRunRequest.ExplorationMode).
    /// </summary>
    public static string BuildBody(string planJson, string? venue, bool compareBoth, bool explore)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(planJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"plan is not valid JSON: {ex.Message}", nameof(planJson));
        }

        if (node is not JsonObject obj)
        {
            throw new ArgumentException("plan must be a JSON object (a StartRunRequest).", nameof(planJson));
        }

        if (!string.IsNullOrWhiteSpace(venue))
        {
            obj["venue"] = venue;
        }
        if (compareBoth)
        {
            obj["compareBoth"] = true;
        }
        if (explore)
        {
            obj["explorationMode"] = true;
            obj["recordExcursions"] = true;
        }

        return obj.ToJsonString();
    }

    /// <summary>Pull the started run's id + status out of the <c>/api/runs</c> POST response.</summary>
    public static (string RunId, string Status) ParseStartResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (
            RunId: GetString(root, "runId") ?? "",
            Status: GetString(root, "status") ?? "unknown");
    }

    private static string? GetString(JsonElement root, string name)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.String)
                {
                    return prop.Value.GetString();
                }
            }
        }
        return null;
    }
}

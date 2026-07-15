using System.Text.Json;
using System.Text.Json.Nodes;

namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.2 (Q3/Q6) — the <see cref="IPipelineStore"/> backed by <c>/api/research/pipelines</c>. The CLI
/// executor persists ALL pipeline state through the running app (never the DB directly), so the UI
/// (<c>/research</c>, P3.3) sees a live pipeline and a resume re-reads authoritative state.
/// </summary>
public sealed class ApiPipelineStore : IPipelineStore
{
    private readonly ResearchApiClient _client;

    public ApiPipelineStore(ResearchApiClient client) => _client = client;

    public async Task<PipelineRecord> CreateAsync(Playbook playbook, string playbookJson, string? artifactDir, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["name"] = playbook.Name,
            ["playbookJson"] = playbookJson,
            ["artifactDir"] = artifactDir,
            ["steps"] = new JsonArray(playbook.Steps
                .Select(s => (JsonNode)new JsonObject { ["kind"] = s.Kind, ["paramHash"] = s.ParamHash })
                .ToArray()),
        };
        var json = await _client.PostAsync("api/research/pipelines", body.ToJsonString(), ct);
        return ParseRecord(json);
    }

    public async Task<PipelineRecord> GetAsync(Guid id, CancellationToken ct)
    {
        var json = await _client.GetAsync($"api/research/pipelines/{id}", ct);
        return ParseRecord(json);
    }

    public async Task SetPipelineStatusAsync(Guid id, string status, int currentStepIndex, bool completed, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["status"] = status,
            ["currentStepIndex"] = currentStepIndex,
            ["completed"] = completed,
        };
        await _client.PutAsync($"api/research/pipelines/{id}", body.ToJsonString(), ct);
    }

    public async Task SetStepStatusAsync(Guid id, int stepIndex, string status, string? verdictJson, string? artifactPath, string paramHash, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["status"] = status,
            ["verdictJson"] = verdictJson,
            ["artifactPath"] = artifactPath,
            ["paramHash"] = paramHash,
        };
        await _client.PutAsync($"api/research/pipelines/{id}/steps/{stepIndex}", body.ToJsonString(), ct);
    }

    internal static PipelineRecord ParseRecord(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var id = Guid.TryParse(GetString(root, "id"), out var g) ? g : Guid.Empty;
        var name = GetString(root, "name") ?? "";
        var status = GetString(root, "status") ?? "unknown";

        var steps = new List<PipelineStepRecord>();
        if (TryGet(root, "steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in stepsEl.EnumerateArray())
            {
                steps.Add(new PipelineStepRecord(
                    StepIndex: GetInt(s, "stepIndex"),
                    Kind: GetString(s, "kind") ?? "",
                    Status: GetString(s, "status") ?? "pending",
                    ParamHash: GetString(s, "paramHash") ?? "",
                    VerdictJson: GetString(s, "verdictJson")));
            }
        }
        return new PipelineRecord(id, name, status, steps);
    }

    private static string? GetString(JsonElement root, string name) =>
        TryGet(root, name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int GetInt(JsonElement root, string name) =>
        TryGet(root, name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n) ? n : 0;

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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.2 — a playbook is an ordered list of typed steps the executor walks sequentially (PLAN §6). It is
/// deliberately dumb: no DAGs, no parallelism, no retry policies (PLAN §12) — resumability + honest
/// persisted verdicts are the whole value. Each step carries its params and a content
/// <see cref="PlaybookStep.ParamHash"/> so <c>--resume</c> can skip already-passed steps and a changed
/// param invalidates that step and everything downstream.
/// </summary>
public sealed record Playbook(string Name, IReadOnlyList<PlaybookStep> Steps);

/// <summary>One typed step. <see cref="Kind"/> ∈ the closed vocabulary in <see cref="StepKinds"/>.</summary>
public sealed record PlaybookStep(int Index, string Kind, JsonObject Params, bool ContinueOnFail)
{
    /// <summary>
    /// Content hash over (kind + canonical params). The resume invalidation key: two steps with the same
    /// kind and params share a hash, so a resumed pipeline reuses a passed step's verdict; editing any
    /// param changes the hash and forces a re-run of this step and all downstream steps.
    /// </summary>
    public string ParamHash => ComputeHash(Kind, Params);

    private static string ComputeHash(string kind, JsonObject prms)
    {
        // Canonicalize the params so key ordering / whitespace never changes the hash.
        var canonical = CanonicalizeToString(prms);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\n" + canonical));
        return Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }

    private static string CanonicalizeToString(JsonObject obj)
    {
        var sb = new StringBuilder();
        WriteCanonical(obj, sb);
        return sb.ToString();
    }

    private static void WriteCanonical(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case JsonObject o:
                sb.Append('{');
                foreach (var kv in o.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    sb.Append(JsonSerializer.Serialize(kv.Key)).Append(':');
                    WriteCanonical(kv.Value, sb);
                    sb.Append(',');
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                foreach (var el in a)
                {
                    WriteCanonical(el, sb);
                    sb.Append(',');
                }
                sb.Append(']');
                break;
            case null:
                sb.Append("null");
                break;
            default:
                sb.Append(node.ToJsonString());
                break;
        }
    }
}

/// <summary>The closed set of step kinds the executor understands (PLAN §6 P3.2).</summary>
public static class StepKinds
{
    public const string EnsureData = "ensure-data";
    public const string DataQuality = "data-quality";
    public const string StartRun = "start-run";
    public const string AwaitRun = "await-run";
    public const string AssertGates = "assert-gates";
    public const string Reconcile = "reconcile";
    public const string ExitLabEval = "exitlab-eval";
    public const string WalkForward = "walk-forward";
    public const string ApplyCalibration = "apply-calibration";
    public const string OwnerGate = "owner-gate";
    public const string MetaAllocate = "meta-allocate";
    public const string BlockBootstrap = "block-bootstrap";
    public const string Report = "report";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        EnsureData, DataQuality, StartRun, AwaitRun, AssertGates, Reconcile, ExitLabEval,
        WalkForward, ApplyCalibration, OwnerGate, MetaAllocate, BlockBootstrap, Report,
    };

    public static bool IsKnown(string kind) => All.Contains(kind);
}

public static class PlaybookParser
{
    /// <summary>
    /// Parse a playbook JSON: <c>{ "name": "...", "steps": [ { "kind": "...", "continueOnFail": false,
    /// ...params } ] }</c>. Params are every property of a step object except the reserved <c>kind</c> and
    /// <c>continueOnFail</c> keys. Throws <see cref="ArgumentException"/> on a malformed file or an unknown
    /// step kind — a playbook with a typo'd step must fail loudly at load, never mid-run.
    /// </summary>
    public static Playbook Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"playbook is not valid JSON: {ex.Message}", nameof(json));
        }

        if (root is not JsonObject obj)
        {
            throw new ArgumentException("playbook must be a JSON object.", nameof(json));
        }

        var name = obj["name"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("playbook.name is required.", nameof(json));
        }

        if (obj["steps"] is not JsonArray stepsArray || stepsArray.Count == 0)
        {
            throw new ArgumentException("playbook.steps must be a non-empty array.", nameof(json));
        }

        var steps = new List<PlaybookStep>();
        for (var i = 0; i < stepsArray.Count; i++)
        {
            if (stepsArray[i] is not JsonObject stepObj)
            {
                throw new ArgumentException($"step[{i}] must be an object.", nameof(json));
            }

            var kind = stepObj["kind"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(kind) || !StepKinds.IsKnown(kind))
            {
                throw new ArgumentException($"step[{i}].kind '{kind}' is missing or unknown.", nameof(json));
            }

            var continueOnFail = stepObj["continueOnFail"]?.GetValue<bool>() ?? false;

            var prms = new JsonObject();
            foreach (var kv in stepObj)
            {
                if (kv.Key is "kind" or "continueOnFail")
                {
                    continue;
                }
                prms[kv.Key] = kv.Value?.DeepClone();
            }

            steps.Add(new PlaybookStep(i, kind, prms, continueOnFail));
        }

        return new Playbook(name, steps);
    }
}

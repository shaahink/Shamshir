using System.Text.Json;

namespace TradingEngine.Infrastructure.Configuration;

/// <summary>
/// iter-quant-model P2.6 (D9, units doctrine). Fails loudly the moment a strategy/pack/profile config sets
/// a raw-pip field without its normalized companion — a flat pip number is silently wrong for symbols whose
/// natural ATR doesn't resemble a forex pair's (gold, crypto). Wired into startup (<see cref="StrategyConfigSeeder"/>)
/// and the <c>lint-config</c> CLI verb so a bad hand-edit is caught before it reaches a running strategy.
///
/// PURE over an already-parsed <see cref="JsonElement"/> so it's trivially unit-testable; <see cref="LintDirectories"/>
/// is the thin I/O wrapper that reads the real config files.
/// </summary>
public static class ConfigLinter
{
    private static readonly (string[] RawPath, string[] CompanionPath, string Description)[] StrategyRules =
    [
        (["positionManagement", "stopLoss", "maxPips"],
         ["positionManagement", "stopLoss", "maxSlAtrMultiple"],
         "positionManagement.stopLoss.maxPips"),
        (["positionManagement", "breakeven", "offsetPips"],
         ["positionManagement", "breakeven", "offsetSpreadMultiple"],
         "positionManagement.breakeven.offsetPips"),
        (["positionManagement", "trailing", "stepPips"],
         ["positionManagement", "trailing", "stepAtrFraction"],
         "positionManagement.trailing.stepPips"),
        (["orderEntry", "limitOffsetPips"],
         ["orderEntry", "limitOffsetAtrFraction"],
         "orderEntry.limitOffsetPips"),
        (["orderEntry", "maxSlippagePips"],
         ["orderEntry", "maxSlippageSpreadMultiple"],
         "orderEntry.maxSlippagePips"),
    ];

    private static readonly (string[] RawPath, string[] CompanionPath, string Description)[] RiskProfileRules =
    [
        (["maxSlPips"], ["maxSlAtrMultiple"], "maxSlPips"),
    ];

    public static IReadOnlyList<string> LintStrategyJson(JsonElement root, string id) =>
        LintRules(root, StrategyRules, "strategy", id);

    public static IReadOnlyList<string> LintRiskProfileJson(JsonElement root, string id) =>
        LintRules(root, RiskProfileRules, "risk-profile", id);

    /// <summary>Reads the real config directories off disk and lints every file. Used by both the startup
    /// check and the <c>lint-config</c> CLI verb.</summary>
    public static IReadOnlyList<string> LintDirectories(string strategiesDir, string riskProfilesDir)
    {
        var violations = new List<string>();
        violations.AddRange(LintDirectory(strategiesDir, LintStrategyJson));
        violations.AddRange(LintDirectory(riskProfilesDir, LintRiskProfileJson));
        return violations;
    }

    private static IEnumerable<string> LintDirectory(string dir, Func<JsonElement, string, IReadOnlyList<string>> lint)
    {
        if (!Directory.Exists(dir)) yield break;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) && idEl.GetString() is { } s
                ? s
                : Path.GetFileNameWithoutExtension(file);

            foreach (var v in lint(root, id))
                yield return v;
        }
    }

    private static IReadOnlyList<string> LintRules(
        JsonElement root, (string[] RawPath, string[] CompanionPath, string Description)[] rules,
        string kind, string id)
    {
        var violations = new List<string>();
        foreach (var (rawPath, companionPath, description) in rules)
        {
            if (TryNavigate(root, rawPath) && !TryNavigate(root, companionPath))
            {
                violations.Add(
                    $"{kind} '{id}': raw-pip field '{description}' is set without its normalized companion " +
                    $"'{string.Join('.', companionPath)}' (D9 units doctrine — see docs/iterations/iter-quant-model/PLAN.md P2.6).");
            }
        }
        return violations;
    }

    private static bool TryNavigate(JsonElement root, string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return false;
        }
        return true;
    }
}

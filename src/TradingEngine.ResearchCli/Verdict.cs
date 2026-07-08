namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.1 (Q3) — every ResearchCli command prints exactly ONE machine verdict as its LAST stdout line:
/// <c>VERDICT: PASS|FAIL key=value key=value …</c>. This is the contract the pipeline agent (P3.2) and
/// a human both read; it is deliberately grep-stable so the agent never has to "go look in the UI"
/// (the whole reason the driving surface is CLI+HTTP, not Angular — R6). The process exit code mirrors
/// the verdict (0 = PASS, non-zero = FAIL) so a playbook step can branch on it without parsing.
/// </summary>
public sealed record Verdict(bool Pass, IReadOnlyList<VerdictField> Fields)
{
    public static Verdict Passing(params VerdictField[] fields) => new(true, fields);

    public static Verdict Failing(params VerdictField[] fields) => new(false, fields);

    public int ExitCode => Pass ? 0 : 1;

    public string Render()
    {
        var head = Pass ? "PASS" : "FAIL";
        if (Fields.Count == 0)
        {
            return $"VERDICT: {head}";
        }
        var kv = string.Join(" ", Fields.Select(f => $"{f.Key}={Escape(f.Value)}"));
        return $"VERDICT: {head} {kv}";
    }

    // A value with whitespace would break the space-delimited key=value grammar; quote it so a
    // downstream parser stays trivial (split on space, then on the first '=').
    private static string Escape(string value) =>
        value.Contains(' ') || value.Contains('"')
            ? "\"" + value.Replace("\"", "'") + "\""
            : value;
}

public readonly record struct VerdictField(string Key, string Value)
{
    public static VerdictField Of(string key, string value) => new(key, value);

    public static VerdictField Of(string key, int value) =>
        new(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

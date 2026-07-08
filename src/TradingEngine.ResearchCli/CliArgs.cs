namespace TradingEngine.ResearchCli;

/// <summary>
/// P3.1 — a deliberately tiny, dependency-free arg model (no System.CommandLine — the repo has zero CLI
/// framework refs and this stays that way). Grammar: leading positional tokens (the verb path, e.g.
/// <c>run validate</c>), then <c>--key value</c> options and <c>--flag</c> booleans. Pure + testable.
/// </summary>
public sealed class CliArgs
{
    private readonly List<string> _positionals = [];
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Positionals => _positionals;

    public static CliArgs Parse(IReadOnlyList<string> args)
    {
        var parsed = new CliArgs();
        var i = 0;
        var sawOption = false;
        while (i < args.Count)
        {
            var token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                sawOption = true;
                var key = token[2..];
                var eq = key.IndexOf('=');
                if (eq >= 0)
                {
                    parsed._options[key[..eq]] = key[(eq + 1)..];
                    i++;
                    continue;
                }
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    parsed._options[key] = args[i + 1];
                    i += 2;
                    continue;
                }
                parsed._flags.Add(key);
                i++;
                continue;
            }
            if (!sawOption)
            {
                parsed._positionals.Add(token);
            }
            i++;
        }
        return parsed;
    }

    public string? Option(string key) => _options.TryGetValue(key, out var v) ? v : null;

    public string Option(string key, string fallback) => _options.TryGetValue(key, out var v) ? v : fallback;

    public int Option(string key, int fallback) =>
        _options.TryGetValue(key, out var v)
        && int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : fallback;

    public bool Flag(string key) => _flags.Contains(key);

    /// <summary>The verb path is the joined positional tokens, e.g. "run validate".</summary>
    public string Verb => string.Join(' ', _positionals.Take(2));
}

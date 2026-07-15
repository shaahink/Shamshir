namespace TradingEngine.Infrastructure.Persistence;

/// <summary>
/// Single source of truth for the physical <c>trading.db</c> (and its sibling <c>marketdata.db</c>)
/// path, shared by the Web app, the Host CLI and the backtest orchestrator (iter-parity-pipeline P1.1,
/// AUDIT F10). Resolution is anchored to the repository root, NOT the process current-directory, so the
/// Web app and the Host CLI open the exact same file regardless of which directory they are launched
/// from — the historical "two databases" split (root <c>data/trading.db</c> vs
/// <c>src/TradingEngine.Web/data/trading.db</c>) is eliminated.
/// </summary>
public static class DbPathResolver
{
    /// <summary>The canonical trading DB location, relative to the repo root.</summary>
    public const string CanonicalTradingDbRelative = "src/TradingEngine.Web/data/trading.db";

    /// <summary>
    /// Resolve the absolute trading DB path.
    /// <list type="bullet">
    /// <item>A rooted <paramref name="configuredPath"/> is returned as-is (tests / explicit overrides).</item>
    /// <item>A relative <paramref name="configuredPath"/> is resolved against the repo root (cwd-independent).</item>
    /// <item>When no value is configured, the canonical location is used.</item>
    /// </list>
    /// </summary>
    public static string ResolveTradingDbPath(string? configuredPath)
    {
        var root = FindRepoRoot();
        if (string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(Path.Combine(root, CanonicalTradingDbRelative));
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(root, configuredPath));
    }

    /// <summary>
    /// Resolve the absolute market-data DB path. Honors an explicit <paramref name="configuredPath"/>
    /// (rooted → as-is, relative → repo-root anchored); otherwise defaults to <c>marketdata.db</c>
    /// sitting alongside the resolved trading DB, matching the Web app's convention.
    /// </summary>
    public static string ResolveMarketDataDbPath(string? configuredPath, string tradingDbPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var root = FindRepoRoot();
            return Path.IsPathRooted(configuredPath)
                ? Path.GetFullPath(configuredPath)
                : Path.GetFullPath(Path.Combine(root, configuredPath));
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(tradingDbPath))
            ?? throw new InvalidOperationException($"Could not derive a directory from '{tradingDbPath}'.");
        return Path.Combine(dir, "marketdata.db");
    }

    /// <summary>
    /// Walk up from the running assembly's base directory until the repo root is found (marked by
    /// <c>TradingEngine.slnx</c> or a <c>.git</c> directory). Falls back to the historical
    /// five-levels-up heuristic if no marker is found, so resolution degrades gracefully.
    /// </summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TradingEngine.slnx"))
                || Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }
}

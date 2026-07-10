using TradingEngine.Infrastructure.Configuration;

namespace TradingEngine.Tests.Integration.InfrastructureTests;

/// <summary>
/// P2.6 gate (D9, units doctrine): the REAL shipped config/strategies/*.json and config/risk-profiles/*.json
/// files must lint clean — every raw-pip field the owner has explicitly set carries its normalized
/// companion. This is the regression net: a future hand-edit that reintroduces a bare raw-pip field (e.g.
/// a new strategy config with only `maxSlippagePips`) fails this test instead of silently shipping a config
/// that crushes gold/crypto stops.
/// </summary>
public sealed class ConfigLinterRealFilesTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "config", "strategies")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not locate repo root (config/strategies) from test output dir.");
    }

    [Fact]
    public void LintDirectories_RealShippedConfigs_HaveNoViolations()
    {
        var root = RepoRoot();

        var violations = ConfigLinter.LintDirectories(
            Path.Combine(root, "config", "strategies"),
            Path.Combine(root, "config", "risk-profiles"));

        violations.Should().BeEmpty(
            "every raw-pip field explicitly set in a shipped config must carry its D9 normalized companion");
    }
}

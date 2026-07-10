using TradingEngine.Infrastructure.Persistence;

namespace TradingEngine.Tests.Unit.Infrastructure;

/// <summary>
/// P1.1 (F10): the single-source DB path resolver must be cwd-independent (repo-root anchored) so the Web
/// app and the Host CLI open the exact same trading.db.
/// </summary>
public sealed class DbPathResolverTests
{
    [Fact]
    public void FindRepoRoot_LocatesTheRepositoryRoot()
    {
        var root = DbPathResolver.FindRepoRoot();

        (File.Exists(Path.Combine(root, "TradingEngine.slnx"))
            || Directory.Exists(Path.Combine(root, ".git")))
            .Should().BeTrue("the resolver must anchor to the repo root marker, not the process cwd");
    }

    [Fact]
    public void ResolveTradingDbPath_NullConfig_ReturnsCanonicalUnderWebProject()
    {
        var resolved = DbPathResolver.ResolveTradingDbPath(null);

        Path.IsPathRooted(resolved).Should().BeTrue();
        resolved.Replace('\\', '/')
            .Should().EndWith("src/TradingEngine.Web/data/trading.db");
        Path.GetFullPath(Path.Combine(DbPathResolver.FindRepoRoot(), DbPathResolver.CanonicalTradingDbRelative))
            .Should().Be(resolved);
    }

    [Fact]
    public void ResolveTradingDbPath_AbsoluteConfig_ReturnedAsIs()
    {
        var absolute = Path.Combine(Path.GetTempPath(), $"shamshir_{Guid.NewGuid():N}.db");

        DbPathResolver.ResolveTradingDbPath(absolute)
            .Should().Be(Path.GetFullPath(absolute));
    }

    [Fact]
    public void ResolveTradingDbPath_RelativeConfig_AnchoredToRepoRootNotCwd()
    {
        var expected = Path.GetFullPath(Path.Combine(DbPathResolver.FindRepoRoot(), "data", "custom.db"));

        DbPathResolver.ResolveTradingDbPath("data/custom.db")
            .Should().Be(expected);
    }

    [Fact]
    public void ResolveMarketDataDbPath_NoConfig_IsSiblingOfTradingDb()
    {
        var tradingDb = Path.Combine(Path.GetTempPath(), "shamshirdata", "trading.db");

        var md = DbPathResolver.ResolveMarketDataDbPath(null, tradingDb);

        md.Should().Be(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(tradingDb))!, "marketdata.db"));
    }

    [Fact]
    public void ResolveMarketDataDbPath_AbsoluteConfig_ReturnedAsIs()
    {
        var absolute = Path.Combine(Path.GetTempPath(), $"md_{Guid.NewGuid():N}.db");

        DbPathResolver.ResolveMarketDataDbPath(absolute, "ignored/trading.db")
            .Should().Be(Path.GetFullPath(absolute));
    }
}

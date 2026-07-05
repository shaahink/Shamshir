using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Infrastructure.Configuration;
using TradingEngine.Infrastructure.Persistence.Repositories;
using TradingEngine.Tests.Integration.Support;

namespace TradingEngine.Tests.Integration.InfrastructureTests;

/// <summary>
/// P2.5 gate: proves the seeder actually parses `thesis`/`expectedTradesPerWeek`/`expectedHoldBars` from
/// the REAL `config/strategies/*.json` files (not a synthetic fixture) — every one of the 9 files was
/// edited to carry a falsifiable one-sentence thesis; this fails loudly if any of them regresses to
/// missing/malformed metadata (e.g. a bad edit breaking JSON parsing for that one field).
/// </summary>
public sealed class StrategyConfigSeederTests : IDisposable
{
    private readonly SqliteInMemory _mem = new();

    public void Dispose() => _mem.Dispose();

    private static string RepoRoot()
    {
        // Walk up from the test assembly's output dir to find the repo root (has a "config" folder).
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "config", "strategies")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not locate repo root (config/strategies) from test output dir.");
    }

    [Fact]
    public async Task SeedAsync_AllNineStrategies_HaveNonEmptyThesisMetadata()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TradingEngine.Infrastructure.Persistence.TradingDbContext>(o => o.UseSqlite(_mem.Connection));
        services.AddScoped<IStrategyConfigStore, SqliteStrategyConfigStore>();
        var provider = services.BuildServiceProvider();

        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var seeder = new StrategyConfigSeeder(scopeFactory, RepoRoot(), NullLogger<StrategyConfigSeeder>.Instance);

        await seeder.SeedAsync(CancellationToken.None);

        using var verifyScope = provider.CreateScope();
        var store = verifyScope.ServiceProvider.GetRequiredService<IStrategyConfigStore>();
        var all = await store.GetAllAsync(CancellationToken.None);

        all.Should().HaveCount(9, "all 9 shipped strategy config files must seed");
        foreach (var entry in all)
        {
            entry.Thesis.Should().NotBeNullOrWhiteSpace($"{entry.Id} must state its falsifiable thesis (P2.5/D-thesis)");
            entry.ExpectedTradesPerWeek.Should().NotBeNull($"{entry.Id} must state an expected trade frequency");
            entry.ExpectedHoldBars.Should().NotBeNull($"{entry.Id} must state an expected hold duration");
        }
    }
}

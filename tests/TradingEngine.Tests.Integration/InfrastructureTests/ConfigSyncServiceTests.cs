using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TradingEngine.Infrastructure.Configuration;
using TradingEngine.Tests.Integration.Support;

namespace TradingEngine.Tests.Integration.InfrastructureTests;

/// <summary>
/// P1.2 (F9): the config sync service must propagate config/*.json edits into the DB on startup, while
/// never clobbering a UI hand-edit. Uses a real SQLite DB (the runtime store) + temp copies of the real
/// config files, edited on disk.
/// </summary>
public sealed class ConfigSyncServiceTests : IDisposable
{
    private readonly SqliteInMemory _mem = new();
    private readonly string _baseDir;
    private readonly string _stratFile;
    private readonly string _riskFile;

    public ConfigSyncServiceTests()
    {
        var root = DbPathResolver.FindRepoRoot();
        _baseDir = Path.Combine(Path.GetTempPath(), $"cfgsync_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_baseDir, "config", "strategies"));
        Directory.CreateDirectory(Path.Combine(_baseDir, "config", "risk-profiles"));
        _stratFile = Path.Combine(_baseDir, "config", "strategies", "trend-breakout.json");
        _riskFile = Path.Combine(_baseDir, "config", "risk-profiles", "standard.json");
        File.Copy(Path.Combine(root, "config", "strategies", "trend-breakout.json"), _stratFile);
        File.Copy(Path.Combine(root, "config", "risk-profiles", "standard.json"), _riskFile);
    }

    private ConfigSyncService NewService(out TradingDbContext ctx)
    {
        ctx = _mem.NewContext();
        return new ConfigSyncService(
            ctx,
            new SqliteStrategyConfigStore(ctx),
            new SqliteRiskProfileStore(ctx, NullLogger<SqliteRiskProfileStore>.Instance),
            _baseDir,
            NullLogger<ConfigSyncService>.Instance);
    }

    private async Task SeedFromFilesAsync()
    {
        var ctx = _mem.NewContext();
        var ss = new SqliteStrategyConfigStore(ctx);
        var rs = new SqliteRiskProfileStore(ctx, NullLogger<SqliteRiskProfileStore>.Instance);
        await ss.UpsertAsync(StrategyConfigSeeder.ParseFile(_stratFile), default);
        var profile = JsonSerializer.Deserialize<TradingEngine.Domain.RiskProfile>(
            File.ReadAllText(_riskFile),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })!;
        await rs.UpsertAsync(profile, default);
    }

    private StrategyConfigEntity Strat() =>
        _mem.NewContext().StrategyConfigs.AsNoTracking().Single(s => s.Id == "trend-breakout");

    [Fact]
    public async Task Baseline_thenInSync_leavesContentAndVersionUntouched()
    {
        await SeedFromFilesAsync();
        Strat().SeededHash.Should().BeNull("freshly seeded rows have no recorded seed hash yet");

        var svc1 = NewService(out _);
        var r1 = await svc1.SyncAsync();
        r1.Baselined.Should().Be(2, "both the strategy and the risk profile get their hash baselined");

        var afterBaseline = Strat();
        afterBaseline.SeededHash.Should().NotBeNull();
        afterBaseline.Version.Should().Be(1, "baseline must not bump Version");
        afterBaseline.OrderEntryJson.Should().Contain("\"Method\":0", "content untouched (Market)");

        var svc2 = NewService(out _);
        var r2 = await svc2.SyncAsync();
        r2.InSync.Should().Be(2);
        r2.Resynced.Should().Be(0);
        Strat().Version.Should().Be(1, "an unchanged file must not bump Version");
    }

    [Fact]
    public async Task JsonEdit_notHandEdited_propagatesToDb_andBumpsVersion()
    {
        await SeedFromFilesAsync();
        await (NewService(out _)).SyncAsync();  // baseline

        // The F9 scenario: flip the order-entry method on disk.
        var edited = File.ReadAllText(_stratFile).Replace("\"Market\"", "\"LimitOffset\"");
        edited.Should().NotBe(File.ReadAllText(_stratFile), "the edit must actually change the file");
        File.WriteAllText(_stratFile, edited);

        var result = await (NewService(out _)).SyncAsync();

        result.Resynced.Should().BeGreaterThanOrEqualTo(1);
        var row = Strat();
        row.OrderEntryJson.Should().Contain("\"Method\":1", "LimitOffset must have propagated into the DB");
        row.Version.Should().Be(2, "a propagated JSON edit bumps Version");
        row.SeededHash.Should().Be(ConfigSyncService.HashFile(_stratFile));
    }

    [Fact]
    public async Task JsonEdit_handEditedSinceSeed_isNotClobbered_andReportedAsDrift()
    {
        await SeedFromFilesAsync();
        await (NewService(out _)).SyncAsync();  // baseline

        // Simulate a UI hand-edit AFTER the seed: change content + move UpdatedAtUtc well past SeededAtUtc.
        {
            var ctx = _mem.NewContext();
            var handRow = ctx.StrategyConfigs.Single(s => s.Id == "trend-breakout");
            handRow.DisplayName = "HAND EDITED";
            handRow.UpdatedAtUtc = (handRow.SeededAtUtc ?? DateTime.UtcNow).AddHours(1);
            await ctx.SaveChangesAsync();
        }

        // Now the file changes on disk too — a genuine conflict.
        File.WriteAllText(_stratFile,
            File.ReadAllText(_stratFile).Replace("\"Market\"", "\"LimitOffset\""));

        var result = await (NewService(out _)).SyncAsync();

        result.Conflicts.Should().Be(1);
        result.Drift.Should().ContainSingle(d => d.Id == "trend-breakout" && d.ConfigType == "strategy");

        var row = Strat();
        row.DisplayName.Should().Be("HAND EDITED", "a hand-edited row must not be clobbered by the JSON");
        row.OrderEntryJson.Should().Contain("\"Method\":0", "the LimitOffset file edit must NOT have been applied");

        // The read-only drift endpoint reports the same conflict without mutating.
        var drift = await (NewService(out _)).DetectDriftAsync();
        drift.Conflicts.Should().Be(1);
    }

    public void Dispose() => _mem.Dispose();
}

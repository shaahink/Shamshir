using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace TradingEngine.Tests.Integration.Api;

/// <summary>
/// iter-36 K6 — the run "duplicate with changes" endpoint + dataset/config identity + lineage. Drives the
/// real Web API (WebApplicationFactory) against a temp, freshly-migrated+seeded DB. Proves: a duplicate
/// keeps the source's DatasetId (same data window), gets a different ConfigSetId (config changed via the
/// risk-profile override), and records ParentRunId = source. Fills the K6 test gap flagged in the iter-36
/// review. (Runs are credential-free: the replay-no-bars run still writes its start record with identity.)
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class DuplicateRunE2ETests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _tempDir;
    private readonly string _tempDb;

    public DuplicateRunE2ETests(WebApplicationFactory<Program> factory)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"shamshir-dup-{Guid.NewGuid():N}");
        _tempDb = Path.Combine(_tempDir, "trading.db");
        Directory.CreateDirectory(_tempDir);

        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Persistence:DbPath", _tempDb);
            builder.UseSetting("CTrader:StartEngineSubprocess", "false");
        }).CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task DuplicateRun_KeepsDataset_NewConfig_SetsParentLineage()
    {
        // Two distinct seeded risk profiles so the duplicate's effective config (hence ConfigSetId) differs.
        var profiles = await GetRiskProfileIdsAsync();
        profiles.Count.Should().BeGreaterThanOrEqualTo(2, "the DB seeds multiple risk profiles");
        var (sourceProfile, dupProfile) = (profiles[0], profiles[1]);

        // 1. Start the source run.
        var sourceId = await StartRunAsync(sourceProfile);
        var source = await WaitForRunAsync(sourceId);
        source.DatasetId.Should().NotBeNullOrEmpty("the run is content-addressed at start (K6)");
        source.ConfigSetId.Should().NotBeNullOrEmpty();
        source.ParentRunId.Should().BeNull("a fresh run has no parent");

        // 2. Duplicate it with a different risk profile.
        var dupResp = await _client.PostAsync(
            $"/api/runs/{sourceId}/duplicate",
            new StringContent(JsonSerializer.Serialize(new { riskProfileId = dupProfile }), Encoding.UTF8, "application/json"));
        dupResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var newId = JsonDocument.Parse(await dupResp.Content.ReadAsStringAsync()).RootElement.GetProperty("runId").GetString();
        newId.Should().NotBeNullOrEmpty().And.NotBe(sourceId, "a duplicate is a new run");

        // 3. The new run: same dataset, new config, parent-linked.
        var dup = await WaitForRunAsync(newId!);
        dup.ParentRunId.Should().Be(sourceId, "lineage: the duplicate records its source");
        dup.DatasetId.Should().Be(source.DatasetId, "same data window ⇒ same dataset identity");
        dup.ConfigSetId.Should().NotBe(source.ConfigSetId, "a changed risk profile ⇒ a different config identity");
    }

    private async Task<List<string>> GetRiskProfileIdsAsync()
    {
        var json = await _client.GetStringAsync("/api/risk-profiles");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement
            : doc.RootElement.GetProperty("profiles");
        return root.EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()!)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();
    }

    private async Task<string> StartRunAsync(string riskProfileId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            symbol = "EURUSD",
            period = "h1",
            start = "2024-01-01",
            end = "2024-01-02",
            balance = 100_000,
            riskProfileId,
            venue = "replay",
        });
        var resp = await _client.PostAsync("/api/runs", new StringContent(payload, Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("runId").GetString()!;
    }

    // The start record (with identity) is written on the background run thread, so poll the same temp DB.
    private async Task<BacktestRunEntity> WaitForRunAsync(string runId)
    {
        for (var i = 0; i < 60; i++)
        {
            await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>()
                .UseSqlite($"Data Source={_tempDb}").Options);
            var entity = await db.BacktestRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.RunId == runId && r.DatasetId != null);
            if (entity is not null) return entity;
            await Task.Delay(500);
        }
        throw new Xunit.Sdk.XunitException($"run {runId} start-record (with DatasetId) was not persisted in time");
    }
}

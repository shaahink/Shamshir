using Microsoft.Data.Sqlite;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TradingEngine.Tests.Integration.Api;

/// <summary>
/// iter-38 (Stream PK / U1) — HTTP contract tests for the add-on packs API + the run-with-pack path that the
/// pure unit tests never exercise end to end:
///   • the 3 starter packs are seeded and listed,
///   • the auto-tune preview returns concrete numbers,
///   • Upsert validates the bundle (an enabled add-on with bad numbers is rejected with 400 — iter-38 U1 fill),
///   • a backtest started with usePackId is accepted (the orchestrator pack path binds + doesn't throw).
/// Drives the real Web API over a temp, freshly-migrated + seeded DB.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class AddOnPacksApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _tempDir;
    private readonly string _tempDb;

    public AddOnPacksApiTests(WebApplicationFactory<Program> factory)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"shamshir-packs-{Guid.NewGuid():N}");
        _tempDb = Path.Combine(_tempDir, "trading.db");
        Directory.CreateDirectory(_tempDir);
        _client = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Persistence:DbPath", _tempDb);
            b.UseSetting("CTrader:StartEngineSubprocess", "false");
        }).CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        if (Directory.Exists(_tempDir)) { SqliteConnection.ClearAllPools(); try { Directory.Delete(_tempDir, true); } catch { /* best-effort */ } }
    }

    private static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    [Fact]
    public async Task GetPacks_returns_the_three_seeded_starter_packs()
    {
        var resp = await _client.GetAsync("/api/addons/packs");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var ids = root.EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        ids.Should().Contain(["breakeven-only", "scalp-tight", "runner-aggressive"]);
    }

    [Fact]
    public async Task Preview_returns_tuned_addon_numbers()
    {
        var resp = await _client.GetAsync("/api/addons/preview?tf=H1&atrPips=12&spreadPips=1");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var root = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        root.GetProperty("trailingAtrMultiple").GetDouble().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Preview_with_unknown_timeframe_is_400()
    {
        var resp = await _client.GetAsync("/api/addons/preview?tf=NOPE");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upsert_rejects_an_invalid_bundle_with_400()
    {
        // PartialTp enabled but close fraction is out of (0,1] — must be rejected, not silently stored.
        var body = new
        {
            id = "bad-pack",
            name = "Bad Pack",
            addOns = new { partialTp = new { enabled = true, closeFraction = 2.0 } },
        };

        var resp = await _client.PutAsync("/api/addons/packs/bad-pack", Json(body));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upsert_then_get_roundtrips_a_valid_pack()
    {
        var body = new
        {
            id = "it-pack",
            name = "Integration Pack",
            description = "created by a test",
            addOns = new
            {
                trailing = new { enabled = true, mode = "Custom", method = "AtrMultiple", atrMultiple = 2.0 },
            },
            regimeDetectionEnabled = true,
        };

        (await _client.PutAsync("/api/addons/packs/it-pack", Json(body)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await _client.GetAsync("/api/addons/packs/it-pack");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement;
        root.GetProperty("name").GetString().Should().Be("Integration Pack");
    }

    [Fact]
    public async Task StartRun_with_usePackId_is_accepted()
    {
        var payload = new Dictionary<string, object?>
        {
            ["symbols"] = new[] { "EURUSD" },
            ["periods"] = new[] { "H1" },
            ["start"] = "2024-01-01", ["end"] = "2024-01-02",
            ["balance"] = 100_000, ["venue"] = "replay",
            ["usePackId"] = "runner-aggressive",
            ["disableRegime"] = true,
        };

        var resp = await _client.PostAsync("/api/runs", Json(payload));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var runId = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("runId").GetString();
        runId.Should().NotBeNullOrEmpty();
    }
}

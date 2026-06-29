using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TradingEngine.Tests.Integration.Api;

/// <summary>
/// iter-strategy-system P2 (D5) + carried P1 governor-toggle assertion: a row-based run must persist its
/// full selection (the row plan + run-level venue/risk/governor/regime/money) and surface it on
/// GET /api/runs/{id}. The run itself fails fast ("no bars" on the empty temp DB) — but the start record
/// and its metadata are written first, which is exactly what we assert.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class RunMetadataTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public RunMetadataTests(WebApplicationFactory<Program> factory)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"shamshir-meta-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _client = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Persistence:DbPath", Path.Combine(_tempDir, "trading.db"));
            b.UseSetting("CTrader:StartEngineSubprocess", "false");
        }).CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        if (Directory.Exists(_tempDir)) { try { Directory.Delete(_tempDir, true); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task RowRun_persists_and_surfaces_full_selection()
    {
        var body = new
        {
            start = "2024-01-01",
            end = "2024-01-31",
            balance = 50_000,
            commissionPerMillion = 25,
            spreadPips = 1.5,
            venue = "replay",
            governorEnabled = false,
            rows = new[]
            {
                new { strategyId = "trend-breakout", symbol = "EURUSD", timeframe = "H1", packId = (string?)null, enabled = true },
                new { strategyId = "super-trend", symbol = "GBPUSD", timeframe = "H4", packId = (string?)"tight", enabled = true },
                new { strategyId = "macd-momentum", symbol = "EURUSD", timeframe = "H1", packId = (string?)null, enabled = false }, // dropped
            },
        };

        var post = await _client.PostAsync("/api/runs",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        post.StatusCode.Should().Be(HttpStatusCode.OK);

        var runId = JsonDocument.Parse(await post.Content.ReadAsStringAsync())
            .RootElement.GetProperty("runId").GetString();
        runId.Should().NotBeNullOrEmpty();

        // The start record is written by the background run task; poll briefly until it lands.
        JsonElement run = default;
        for (var i = 0; i < 40; i++)
        {
            var get = await _client.GetAsync($"/api/runs/{runId}");
            if (get.StatusCode == HttpStatusCode.OK)
            {
                run = JsonDocument.Parse(await get.Content.ReadAsStringAsync()).RootElement.Clone();
                break;
            }
            await Task.Delay(100);
        }

        run.ValueKind.Should().Be(JsonValueKind.Object, "the run start record should be persisted");

        run.GetProperty("venue").GetString().Should().Be("replay");
        run.GetProperty("governorEnabled").GetBoolean().Should().BeFalse();
        run.GetProperty("regimeEnabled").GetBoolean().Should().BeTrue();
        run.GetProperty("commissionPerMillion").GetDouble().Should().Be(25);
        run.GetProperty("spreadPips").GetDouble().Should().Be(1.5);

        // The persisted run plan = the TWO enabled rows (the disabled one is dropped), with the per-row pack.
        var plan = JsonDocument.Parse(run.GetProperty("runPlanJson").GetString()!).RootElement;
        plan.GetArrayLength().Should().Be(2);
        var entries = plan.EnumerateArray().ToList();
        entries.Should().Contain(e =>
            e.GetProperty("StrategyId").GetString() == "trend-breakout" &&
            e.GetProperty("Symbol").GetString() == "EURUSD" &&
            e.GetProperty("Timeframe").GetString() == "H1");
        entries.Should().Contain(e =>
            e.GetProperty("StrategyId").GetString() == "super-trend" &&
            e.GetProperty("PackId").GetString() == "tight");
    }
}

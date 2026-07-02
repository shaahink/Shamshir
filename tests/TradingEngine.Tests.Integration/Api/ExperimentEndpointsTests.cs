using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TradingEngine.Tests.Integration.Api;

/// <summary>
/// iter-tape-enable Tier1: end-to-end coverage for the experiment pipeline over imported tape data (no
/// cTrader). Before this fix, ExperimentRunner read bars from the per-run catalog (IBarRepository) instead
/// of the canonical tape store (IMarketDataStore), so imported/downloaded market data was invisible to
/// experiments; a temp-db-only summary lookup also meant Experiment.Runs never persisted. Seeding ONLY the
/// market-data store (never IBarRepository/trading.db Bars) means a regression back to the catalog path
/// fails this test with "No tape data found" instead of silently passing.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class ExperimentEndpointsTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _configuredFactory;
    private readonly HttpClient _client;
    private readonly string _tempDir;
    private readonly string _tempDb;
    private static readonly Symbol Eur = Symbol.Parse("EURUSD");

    public ExperimentEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"shamshir-exp-{Guid.NewGuid():N}");
        _tempDb = Path.Combine(_tempDir, "trading.db");
        Directory.CreateDirectory(_tempDir);
        _configuredFactory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Persistence:DbPath", _tempDb);
            b.UseSetting("CTrader:StartEngineSubprocess", "false");
        });
        _client = _configuredFactory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _configuredFactory.Dispose();
        if (Directory.Exists(_tempDir)) { try { Directory.Delete(_tempDir, true); } catch { /* best-effort */ } }
    }

    private async Task SeedTapeBarsAsync(DateTime from, int count)
    {
        var store = _configuredFactory.Services.GetRequiredService<IMarketDataStore>();
        var bars = new List<Bar>(count);
        var close = 1.1000m;
        var rng = new Random(42);
        for (var i = 0; i < count; i++)
        {
            var open = close;
            var drift = (decimal)(rng.NextDouble() - 0.48) * 0.0015m;
            close = open + drift;
            var high = Math.Max(open, close) + 0.0004m;
            var low = Math.Min(open, close) - 0.0004m;
            bars.Add(new Bar(Eur, Timeframe.H1, from.AddHours(i), open, high, low, close, 100));
        }
        await store.WriteBarsAsync("test", bars);
    }

    [Fact]
    public async Task Create_RunsOverTapeDataAndPersistsRunRows()
    {
        var from = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedTapeBarsAsync(from, 400); // ~16.6 days of H1 — clears the 50-bar MA warm-up with room to trade

        var experimentName = $"tier1-smoke-{Guid.NewGuid():N}";
        var spec = new
        {
            name = experimentName,
            hypothesis = "trend-breakout trades against imported tape data end-to-end",
            symbols = new[] { "EURUSD" },
            timeframes = new[] { "H1" },
            strategies = new[] { "trend-breakout" },
            from = "2024-01-01",
            to = "2024-01-16",
            variants = new[] { new { label = "baseline" } },
        };

        try
        {
            var resp = await _client.PostAsync("/api/experiments",
                new StringContent(JsonSerializer.Serialize(spec), Encoding.UTF8, "application/json"));
            var body = await resp.Content.ReadAsStringAsync();
            resp.StatusCode.Should().Be(HttpStatusCode.OK, body);

            var created = JsonDocument.Parse(body).RootElement;
            created.GetProperty("status").GetString().Should().Be("completed");
            var experimentId = created.GetProperty("experimentId").GetGuid();
            var variantScores = created.GetProperty("variantScores").EnumerateArray().ToList();
            variantScores.Should().HaveCount(1);

            var detailResp = await _client.GetAsync($"/api/experiments/{experimentId}");
            detailResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var detail = JsonDocument.Parse(await detailResp.Content.ReadAsStringAsync()).RootElement;

            detail.GetProperty("status").GetString().Should().Be("Completed");
            var runs = detail.GetProperty("runs").EnumerateArray().ToList();
            runs.Should().NotBeEmpty("ExperimentRunner should persist a run row per fold instead of leaving Experiment.Runs empty");

            var scoreJson = runs[0].GetProperty("scoreJson").GetString();
            scoreJson.Should().NotBe("{}", "each persisted run should carry its real FoldScore, not the AddRunAsync default");
            var act = () => JsonDocument.Parse(scoreJson!);
            act.Should().NotThrow("scoreJson should be a well-formed serialized FoldScore");
        }
        finally
        {
            var shortId = experimentName; // report dir uses spec.Name verbatim, which is unique per test run
            var solRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var reportDirPrefix = Path.Combine(solRoot, "docs", "experiments");
            if (Directory.Exists(reportDirPrefix))
            {
                foreach (var dir in Directory.GetDirectories(reportDirPrefix, $"{shortId}-*"))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
                }
            }
        }
    }

    [Fact]
    public async Task Create_WithoutTapeData_Returns400WithClearError()
    {
        var spec = new
        {
            name = $"tier1-no-data-{Guid.NewGuid():N}",
            hypothesis = "no bars imported for this symbol/window",
            symbols = new[] { "EURUSD" },
            timeframes = new[] { "H1" },
            strategies = new[] { "trend-breakout" },
            from = "2099-01-01",
            to = "2099-01-08",
            variants = new[] { new { label = "baseline" } },
        };

        var resp = await _client.PostAsync("/api/experiments",
            new StringContent(JsonSerializer.Serialize(spec), Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("No tape data found");
    }
}

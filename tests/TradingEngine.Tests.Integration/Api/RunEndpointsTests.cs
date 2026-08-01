using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TradingEngine.Tests.Integration.Api;

/// <summary>
/// iter-37 — HTTP contract tests for the new / modified run endpoints (analytics-strategies funnel,
/// NDJSON journal export, equity, CSV export, strategyOverrides). Contract/shape only: each endpoint
/// must exist, return the right status + content-type, and a well-formed (possibly empty) body — robust
/// without seeded catalog bars. Drives the real Web API over a temp, freshly-migrated DB.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class RunEndpointsTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _tempDir;
    private readonly string _tempDb;

    public RunEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"shamshir-eps-{Guid.NewGuid():N}");
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
        if (Directory.Exists(_tempDir)) { try { Directory.Delete(_tempDir, true); } catch { /* best-effort */ } }
    }

    private async Task<string> StartRunAsync(object? overrides = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["symbol"] = "EURUSD", ["period"] = "h1",
            ["start"] = "2024-01-01", ["end"] = "2024-01-02",
            ["balance"] = 100_000, ["venue"] = "replay",
        };
        if (overrides is not null) payload["strategyOverrides"] = overrides;

        var resp = await _client.PostAsync("/api/runs",
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.GetProperty("runId").GetString()!;
    }

    [Fact]
    public async Task StrategyBreakdown_Returns200JsonArray()
    {
        var runId = await StartRunAsync();
        var resp = await _client.GetAsync($"/api/runs/{runId}/analytics/strategies");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).TrimStart().Should().StartWith("[");
    }

    [Fact]
    public async Task Journal_Returns200JsonArray()
    {
        var runId = await StartRunAsync();
        var resp = await _client.GetAsync($"/api/runs/{runId}/journal?limit=50");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).TrimStart().Should().StartWith("[");
    }

    [Fact]
    public async Task JournalExport_Returns200Ndjson()
    {
        var runId = await StartRunAsync();
        var resp = await _client.GetAsync($"/api/runs/{runId}/journal/export");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/x-ndjson");
        // Every non-empty line must be a parseable JSON object (possibly zero lines for an empty journal).
        var body = await resp.Content.ReadAsStringAsync();
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var act = () => JsonDocument.Parse(line);
            act.Should().NotThrow("each NDJSON line is a JSON object");
        }
    }

    [Fact]
    public async Task Equity_Returns200JsonArray()
    {
        var runId = await StartRunAsync();
        var resp = await _client.GetAsync($"/api/runs/{runId}/equity");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).TrimStart().Should().StartWith("[");
    }

    [Fact]
    public async Task TradesCsv_Returns200WithHeader()
    {
        var runId = await StartRunAsync();
        var resp = await _client.GetAsync($"/api/export/trades.csv?runId={runId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        (await resp.Content.ReadAsStringAsync()).Should().StartWith("Symbol,Direction,Lots,EntryPrice");
    }

    [Fact]
    public async Task TradesCsv_MissingRunId_Returns400()
    {
        var resp = await _client.GetAsync("/api/export/trades.csv");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task StartRun_WithStrategyOverrides_Returns200()
    {
        var runId = await StartRunAsync(overrides: new { meanreversion = new { RsiPeriod = 7 } });
        runId.Should().NotBeNullOrEmpty();
    }
}

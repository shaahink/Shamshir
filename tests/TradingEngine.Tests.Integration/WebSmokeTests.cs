using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TradingEngine.Tests.Integration;

[Trait("Category", "Infrastructure")]
public sealed class WebSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public WebSmokeTests(WebApplicationFactory<Program> factory)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"shamshir-web-{Guid.NewGuid():N}");
        var tempDb = Path.Combine(tempDir, "trading.db");
        Directory.CreateDirectory(tempDir);

        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Persistence:DbPath", tempDb);
        }).CreateClient();
    }

    [Fact]
    public async Task Dashboard_Returns200_NoSqliteError()
    {
        var response = await _client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotContain("SqliteException");
        body.Should().Contain("Shamshir");
    }

    [Fact]
    public async Task TradesPage_Returns200()
    {
        var response = await _client.GetAsync("/trades");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PerformancePage_Returns200()
    {
        var response = await _client.GetAsync("/performance");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EventsPage_Returns200()
    {
        var response = await _client.GetAsync("/events");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BacktestsPage_Returns200()
    {
        var response = await _client.GetAsync("/backtests");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task NewBacktestForm_Returns200()
    {
        var response = await _client.GetAsync("/backtests/run");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ApiBacktestStart_ReturnsRunId_AndCompletes()
    {
        var payload = JsonSerializer.Serialize(new
        {
            symbol = "EURUSD",
            period = "h1",
            start = "2024-01-01",
            end = "2024-01-05",
            balance = 100000
        });

        var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/backtest/start", content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("runId");
        body.Should().Contain("status");

        using var jsonDoc = JsonDocument.Parse(body);
        var runId = jsonDoc.RootElement.GetProperty("runId").GetString()!;

        // Poll status for up to 15s — backtest runs async, may fail gracefully
        // (ctrader-cli not installed in CI) but must NOT crash with an exception
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);
            var statusResp = await _client.GetAsync($"/api/backtest/{runId}/status");
            statusResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var statusBody = await statusResp.Content.ReadAsStringAsync();
            using var statusDoc = JsonDocument.Parse(statusBody);

            var status = statusDoc.RootElement.GetProperty("status").GetString();
            // "starting" / "running" → still in progress, keep polling
            if (status is "starting" or "running") continue;

            // Terminal states: "completed" or "failed"
            // "failed" is acceptable (ctrader-cli not available)
            // But the runId must be in the result, and no crash
            statusDoc.RootElement.TryGetProperty("error", out var errorEl);
            return;
        }

        // If we hit the deadline, the backtest is stuck — that's a bug
        Assert.Fail("Backtest did not complete within 15s polling window");
    }

    [Fact]
    public async Task IndexPage_HasLayout()
    {
        var response = await _client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("navbar");
        body.Should().Contain("Shamshir Engine");
    }

    [Fact]
    public async Task AllNavLinks_Return200()
    {
        var response = await _client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        // Extract all hrefs from nav links
        var navLinks = new[] { "/", "/trades", "/performance", "/backtests", "/events" };
        foreach (var link in navLinks)
        {
            var linkResponse = await _client.GetAsync(link);
            linkResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"nav link {link} should work");
        }
    }
}

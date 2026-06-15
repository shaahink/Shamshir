using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TradingEngine.Tests.Integration;

[Trait("Category", "Infrastructure")]
public sealed class WebSmokeTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public WebSmokeTests(WebApplicationFactory<Program> factory)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"shamshir-web-{Guid.NewGuid():N}");
        var tempDb = Path.Combine(_tempDir, "trading.db");
        Directory.CreateDirectory(_tempDir);

        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Persistence:DbPath", tempDb);
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
    public async Task ApiBacktestStart_ReturnsRunId()
    {
        var payload = JsonSerializer.Serialize(new
        {
            symbol = "EURUSD",
            period = "h1",
            start = "2024-01-01",
            end = "2024-01-02",
            balance = 100000
        });

        var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        var response = await _client.PostAsync("/api/backtest/start", content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("runId");
        body.Should().Contain("status");
    }

    [Fact]
    public async Task IndexPage_HasLayout()
    {
        var response = await _client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("sidebar");
        body.Should().Contain("Shamshir");
    }

    [Fact]
    public async Task AllNavLinks_Return200()
    {
        var response = await _client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        // Extract all hrefs from nav links
        var navLinks = new[] { "/", "/trades", "/performance", "/backtests", "/events", "/strategies", "/compliance" };
        foreach (var link in navLinks)
        {
            var linkResponse = await _client.GetAsync(link);
            linkResponse.StatusCode.Should().Be(HttpStatusCode.OK, $"nav link {link} should work");
        }
    }
}

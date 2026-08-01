using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TradingEngine.Tests.Integration.Api;

/// <summary>
/// iter-37 — the API rejects a structurally-empty/invalid backtest (no symbol, inverted date range, or
/// non-positive balance) with 400, so a degenerate run never starts. The SPA new-backtest also blocks this
/// client-side; this is the backend safety net.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class BacktestStartGuardTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _tempDir;

    public BacktestStartGuardTests(WebApplicationFactory<Program> factory)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"shamshir-guard-{Guid.NewGuid():N}");
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

    private Task<HttpResponseMessage> Post(object body) =>
        _client.PostAsync("/api/runs", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

    [Fact]
    public async Task Rejects_NonPositiveBalance()
    {
        var resp = await Post(new { symbol = "EURUSD", period = "h1", start = "2024-01-01", end = "2024-01-02", balance = 0, venue = "replay" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rejects_InvertedDateRange()
    {
        var resp = await Post(new { symbol = "EURUSD", period = "h1", start = "2024-02-01", end = "2024-01-01", balance = 100_000, venue = "replay" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rejects_NoSymbol()
    {
        var resp = await Post(new { symbol = "", symbols = new string[0], period = "h1", start = "2024-01-01", end = "2024-01-02", balance = 100_000, venue = "replay" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Accepts_ValidRequest()
    {
        var resp = await Post(new { symbol = "EURUSD", period = "h1", start = "2024-01-01", end = "2024-01-02", balance = 100_000, venue = "replay" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

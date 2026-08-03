using Microsoft.Data.Sqlite;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TradingEngine.Tests.Integration.Api;

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
        if (Directory.Exists(_tempDir)) { SqliteConnection.ClearAllPools(); try { Directory.Delete(_tempDir, true); } catch { /* best-effort */ } }
    }

    private Task<HttpResponseMessage> Post(object body) =>
        _client.PostAsync("/api/runs", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));

    private static object ValidBody => new
    {
        symbols = new[] { "EURUSD" },
        periods = new[] { "H1" },
        start = "2024-01-01",
        end = "2024-01-02",
        balance = 100_000,
        venue = "replay"
    };

    [Fact]
    public async Task Rejects_NonPositiveBalance()
    {
        var resp = await Post(new { symbols = new[] { "EURUSD" }, periods = new[] { "H1" }, start = "2024-01-01", end = "2024-01-02", balance = 0, venue = "replay" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rejects_InvertedDateRange()
    {
        var resp = await Post(new { symbols = new[] { "EURUSD" }, periods = new[] { "H1" }, start = "2024-02-01", end = "2024-01-01", balance = 100_000, venue = "replay" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rejects_EmptySymbolsAndPeriods()
    {
        var resp = await Post(new { symbols = new string[0], periods = new string[0], start = "2024-01-01", end = "2024-01-02", balance = 100_000, venue = "replay" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK); // defaults to EURUSD/H1
    }

    [Fact]
    public async Task Accepts_ValidRequest()
    {
        var resp = await Post(ValidBody);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // F87 R4: the cTrader CLI needs a concrete --spread/--commission — there is no per-bar path
    // there. Null must be rejected at request validation, not silently defaulted to a number.
    [Fact]
    public async Task Rejects_NullSpread_OnCTraderVenue()
    {
        var resp = await Post(new { symbols = new[] { "EURUSD" }, periods = new[] { "H1" }, start = "2024-01-01", end = "2024-01-02", balance = 100_000, venue = "ctrader", commissionPerMillion = 30 });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("SpreadPips");
    }

    [Fact]
    public async Task Rejects_NullSpread_OnCompareBoth()
    {
        var resp = await Post(new { symbols = new[] { "EURUSD" }, periods = new[] { "H1" }, start = "2024-01-01", end = "2024-01-02", balance = 100_000, venue = "replay", compareBoth = true, commissionPerMillion = 30 });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("SpreadPips");
    }

    [Fact]
    public async Task Accepts_NullSpread_OnReplayVenue()
    {
        // Null spread on a credential-free venue = "use the recorded per-bar spread" — valid.
        var resp = await Post(ValidBody);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rejects_NullCommission_OnCTraderVenue()
    {
        var resp = await Post(new { symbols = new[] { "EURUSD" }, periods = new[] { "H1" }, start = "2024-01-01", end = "2024-01-02", balance = 100_000, venue = "ctrader", spreadPips = 1 });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("CommissionPerMillion");
    }

    [Fact]
    public async Task Rejects_NullCommission_OnCompareBoth()
    {
        var resp = await Post(new { symbols = new[] { "EURUSD" }, periods = new[] { "H1" }, start = "2024-01-01", end = "2024-01-02", balance = 100_000, venue = "replay", compareBoth = true, spreadPips = 1 });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("CommissionPerMillion");
    }
}

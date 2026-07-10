using Microsoft.Data.Sqlite;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Tests.Integration.Api;

/// <summary>
/// P0 repro: GET /api/runs/{id}/trades must return stopLoss/takeProfit for every trade.
/// Fails because GetRunTradesAsync never maps StopLoss/TakeProfit in the EF projection.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class RunTradesContractTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _configuredFactory;
    private readonly HttpClient _client;
    private readonly string _tempDir;
    private readonly string _tempDb;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    public RunTradesContractTests(WebApplicationFactory<Program> factory)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"shamshir-rtc-{Guid.NewGuid():N}");
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
        if (Directory.Exists(_tempDir)) { SqliteConnection.ClearAllPools(); try { Directory.Delete(_tempDir, true); } catch { /* best-effort */ } }
    }

    private async Task SeedTradeAsync(decimal stopLoss, decimal? takeProfit, string mode = "Market")
    {
        using var scope = _configuredFactory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

        // Ensure DB is created.
        await db.Database.EnsureCreatedAsync();

        var run = new BacktestRunEntity
        {
            RunId = _runId,
            Symbol = "EURUSD",
            Period = "H1",
            StartedAtUtc = DateTime.UtcNow.AddHours(-1),
            CompletedAtUtc = DateTime.UtcNow,
            InitialBalance = 100_000,
            ExitCode = 0,
        };
        db.BacktestRuns.Add(run);

        var trade = new TradeResultEntity
        {
            Id = Guid.NewGuid(),
            PositionId = Guid.NewGuid(),
            OrderId = Guid.NewGuid(),
            Symbol = "EURUSD",
            Direction = "Long",
            Lots = 0.1m,
            EntryPrice = 1.08000m,
            ExitPrice = 1.08500m,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            OpenedAtUtc = DateTime.UtcNow.AddHours(-2),
            ClosedAtUtc = DateTime.UtcNow.AddHours(-1),
            GrossPnLAmount = 50m,
            CommissionAmount = 1.5m,
            SwapAmount = 0.3m,
            NetPnLAmount = 48.2m,
            PnLPips = 50,
            RMultiple = 1.5,
            MaxAdverseExcursion = 10,
            MaxFavorableExcursion = 60,
            ExitReason = "SL",
            StrategyId = "super-trend",
            Mode = mode,
            DurationSeconds = 3600,
            RunId = _runId,
        };
        db.Trades.Add(trade);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task TradesResponse_HasStopLossAndTakeProfit()
    {
        await SeedTradeAsync(stopLoss: 1.16393m, takeProfit: 1.16740m);

        var resp = await _client.GetAsync($"/api/runs/{_runId}/trades");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);

        var trades = doc.RootElement.GetProperty("trades").EnumerateArray().ToList();
        trades.Should().HaveCount(1);

        var trade = trades[0];

        // P0: stopLoss should be a number (not null/undefined -> -)
        var sl = trade.GetProperty("stopLoss");
        sl.ValueKind.Should().Be(JsonValueKind.Number, "stopLoss must be a numeric price");
        sl.GetDecimal().Should().Be(1.16393m);

        // P0: takeProfit should be present
        var tp = trade.GetProperty("takeProfit");
        tp.ValueKind.Should().Be(JsonValueKind.Number, "takeProfit must be a numeric price");
        tp.GetDecimal().Should().Be(1.16740m);
    }

    [Fact]
    public async Task TradesResponse_EntryTypeIsOrderMethodNotRunMode()
    {
        await SeedTradeAsync(stopLoss: 1.16393m, takeProfit: 1.16740m, mode: "Backtest");

        var resp = await _client.GetAsync($"/api/runs/{_runId}/trades");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);

        var trade = doc.RootElement.GetProperty("trades").EnumerateArray().First();

        // P0 / D5: entryType should NOT be "Backtest" (the run mode)
        var entryType = trade.GetProperty("entryType");
        entryType.ValueKind.Should().NotBe(JsonValueKind.Null);
        if (entryType.ValueKind == JsonValueKind.String)
        {
            entryType.GetString().Should().NotBe("Backtest",
                "entryType must be the order entry method (Market/Limit), not the run mode");
        }
    }
}

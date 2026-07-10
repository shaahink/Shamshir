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
/// P0 repro: a completed run's TotalTrades must equal the count of persisted trade rows.
/// Demonstrates D2: summary stats written before venue settlement drains all trades.
/// Seeds 5 trades with a run record that says TotalTrades=0 → mismatch.
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class RunTradeCountTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _configuredFactory;
    private readonly HttpClient _client;
    private readonly string _tempDir;
    private readonly string _tempDb;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];

    public RunTradeCountTests(WebApplicationFactory<Program> factory)
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"shamshir-rtcc-{Guid.NewGuid():N}");
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

    private async Task SeedAsync(int tradeCount)
    {
        using var scope = _configuredFactory.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        await db.Database.EnsureCreatedAsync();

        // Run record with TotalTrades=3 (deliberately incorrect — 5 trades exist).
        // The self-heal in ReconcileAsync only triggers when total==0, exitCode==-1, or completedAt==default.
        // But the cTrader path (D2) writes TotalTrades=3 from GetTradeStatsAsync called BEFORE settlement
        // drains all 5 trades. Since TotalTrades=3 is nonzero, the self-heal DOESN'T fire.
        db.BacktestRuns.Add(new BacktestRunEntity
        {
            RunId = _runId,
            Symbol = "EURUSD",
            Period = "H1",
            StartedAtUtc = DateTime.UtcNow.AddHours(-2),
            CompletedAtUtc = DateTime.UtcNow,
            InitialBalance = 100_000,
            ExitCode = 0,
            TotalTrades = 3, // wrong — only 3 of 5 trades were visible when stats were computed
        });

        for (var i = 0; i < tradeCount; i++)
        {
            db.Trades.Add(new TradeResultEntity
            {
                Id = Guid.NewGuid(),
                PositionId = Guid.NewGuid(),
                OrderId = Guid.NewGuid(),
                Symbol = "EURUSD",
                Direction = i % 2 == 0 ? "Long" : "Short",
                Lots = 0.1m,
                EntryPrice = 1.08000m,
                ExitPrice = 1.08500m,
                StopLoss = 1.07500m,
                TakeProfit = null,
                OpenedAtUtc = DateTime.UtcNow.AddHours(-2),
                ClosedAtUtc = DateTime.UtcNow.AddHours(-1),
                GrossPnLAmount = 50m,
                NetPnLAmount = 48.2m,
                PnLPips = 50,
                ExitReason = "SL",
                StrategyId = "super-trend",
                Mode = "Backtest",
                DurationSeconds = 3600,
                RunId = _runId,
            });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task TradeCountFromRunRecord_MatchesPersistedTradeCount()
    {
        await SeedAsync(tradeCount: 5);

        // Summary TotalTrades from run record.
        var runResp = await _client.GetAsync($"/api/runs/{_runId}");
        var runBody = await runResp.Content.ReadAsStringAsync();
        runResp.StatusCode.Should().Be(HttpStatusCode.OK, $"GET /api/runs/{_runId} returned {runResp.StatusCode}: {runBody}");
        var runDoc = JsonDocument.Parse(runBody);
        var summaryTrades = runDoc.RootElement.GetProperty("totalTrades").GetInt32();

        // Actual trade count from trades endpoint.
        var tradesResp = await _client.GetAsync($"/api/runs/{_runId}/trades");
        var tradesBody = await tradesResp.Content.ReadAsStringAsync();
        tradesResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var tradesDoc = JsonDocument.Parse(tradesBody);
        var actualTrades = tradesDoc.RootElement.GetProperty("totalCount").GetInt32();

        // P0: these should match. FAILS: summary=0 vs persisted=5.
        actualTrades.Should().Be(summaryTrades,
            $"summary TotalTrades ({summaryTrades}) must equal persisted trade count ({actualTrades})");
    }
}

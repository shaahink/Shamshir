using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using TradingEngine.Domain;
using TradingEngine.Infrastructure.Persistence;
using TradingEngine.Infrastructure.Persistence.Entities;
using TradingEngine.Tests.Integration.Support;
using TradingEngine.Web.Api;
using TradingEngine.Web.Dtos.Trades;
using TradingEngine.Web.Services;

namespace TradingEngine.Tests.Integration.Bars;

/// <summary>
/// iter-redesign P6.2 — GET /api/trades/{id}/chart returns the candlestick window around a trade plus
/// entry/exit/SL/TP markers (the data layer for the trade-detail chart UI).
/// </summary>
[Trait("Category", "Infrastructure")]
public sealed class TradeChartTests : IDisposable
{
    private readonly SqliteInMemory _db = new();
    private readonly Guid _tradeId = Guid.NewGuid();
    private static readonly DateTime Entry = new(2024, 1, 1, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Exit = new(2024, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    public TradeChartTests()
    {
        using var db = NewContext();

        db.Trades.Add(new TradeResultEntity
        {
            Id = _tradeId,
            PositionId = Guid.NewGuid(),
            OrderId = Guid.NewGuid(),
            Symbol = "EURUSD",
            Direction = "Long",
            Lots = 0.1m,
            EntryPrice = 1.1000m,
            ExitPrice = 1.0950m,
            StopLoss = 1.0950m,
            TakeProfit = 1.1100m,
            OpenedAtUtc = Entry,
            ClosedAtUtc = Exit,
            ExitReason = "SL",
            StrategyId = "test",
            Mode = "Backtest",
            RunId = "run-chart",
        });

        // 8 hourly EURUSD H1 bars spanning the trade window.
        for (var i = 0; i < 8; i++)
        {
            var time = new DateTime(2024, 1, 1, 3, 0, 0, DateTimeKind.Utc).AddHours(i);
            db.Bars.Add(new BarEntity
            {
                Id = Guid.NewGuid(),
                RunId = "run-chart",
                Symbol = "EURUSD",
                Timeframe = "H1",
                OpenTimeUtc = time,
                Open = 1.1000m, High = 1.1010m, Low = 1.0990m, Close = 1.1000m,
                Volume = 1000,
            });
        }

        db.SaveChanges();
    }

    private TradingDbContext NewContext() => _db.NewContext();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetChart_ReturnsBarsAndEntryExitSlTpMarkers()
    {
        await using var db = NewContext();
        var controller = new TradesController(db, new BarQueryService(db), Substitute.For<IExcursionRepository>());

        var result = await controller.GetChart(_tradeId, padBars: 50, CancellationToken.None);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var chart = ok.Value.Should().BeOfType<TradeChartResponse>().Subject;

        chart.TradeId.Should().Be(_tradeId);
        chart.Symbol.Should().Be("EURUSD");
        chart.Timeframe.Should().Be("H1");
        chart.Direction.Should().Be("Long");

        chart.Bars.Should().NotBeEmpty("the trade window overlaps the seeded bars");
        chart.Bars.Should().HaveCount(8);

        chart.Markers.Should().Contain(m => m.Kind == "Entry" && m.Price == 1.1000m);
        chart.Markers.Should().Contain(m => m.Kind == "Exit" && m.Price == 1.0950m);
        chart.Markers.Should().Contain(m => m.Kind == "StopLoss" && m.Price == 1.0950m);
        chart.Markers.Should().Contain(m => m.Kind == "TakeProfit" && m.Price == 1.1100m);
    }

    [Fact]
    public async Task GetChart_UnknownTrade_ReturnsNotFound()
    {
        await using var db = NewContext();
        var controller = new TradesController(db, new BarQueryService(db), Substitute.For<IExcursionRepository>());

        var result = await controller.GetChart(Guid.NewGuid(), padBars: 50, CancellationToken.None);

        result.Should().BeOfType<NotFoundObjectResult>();
    }
}

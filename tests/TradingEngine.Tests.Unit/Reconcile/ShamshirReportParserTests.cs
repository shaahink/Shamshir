using FluentAssertions;
using TradingEngine.Infrastructure.Reconcile;

namespace TradingEngine.Tests.Unit.Reconcile;

/// <summary>iter-marketdata-tape P0 — parses the cBot's own venue ledger (shamshir-report.json) into the
/// normalized reconcile shape. Fixture matches ShamshirTradeLogger.BuildReport's schema exactly.</summary>
public sealed class ShamshirReportParserTests
{
    // entryTime 2024-01-03T10:00:00Z / closeTime 11:00:00Z as unix ms.
    private const long Entry = 1704276000000;
    private const long Close = 1704279600000;

    private static readonly string Report = $$"""
    {
      "main": { "symbol": "EURUSD", "period": "H1", "netProfit": 30.0, "startingCapital": 10000.0 },
      "tradeStatistics": {
        "netProfit": { "all": 30.0, "long": 50.0, "short": -20.0 },
        "totalTrades": { "all": 2 },
        "winningTrades": { "all": 1 },
        "commissions": { "all": 4.0 },
        "swaps": { "all": 3.0 }
      },
      "equity": { "maxEquityDrawdownPercent": 4.6, "maxEquityDrawdownAbsolute": 460.0, "points": [] },
      "history": { "items": [
        { "id": 1, "direction": "Long",  "net": 50.0,  "gross": 55.0, "commissions": 3.0, "swaps": 2.0,
          "entryPrice": 1.1000, "closePrice": 1.1050, "pips": 50.0, "quantity": 0.10, "entryTime": {{Entry}}, "closeTime": {{Close}} },
        { "id": 2, "direction": "Short", "net": -20.0, "gross": -18.0, "commissions": 1.0, "swaps": 1.0,
          "entryPrice": 1.1050, "closePrice": 1.1070, "pips": -20.0, "quantity": 0.10, "entryTime": {{Entry}}, "closeTime": {{Close}} }
      ] }
    }
    """;

    [Fact]
    public void Parses_totals_and_trades_from_the_oracle_report()
    {
        var ledger = ShamshirReportParser.Parse(Report);

        ledger.Source.Should().Be("ctrader");
        ledger.NetProfit.Should().Be(30.0m, "tradeStatistics.netProfit.all is authoritative");
        ledger.GrossProfit.Should().Be(37.0m, "55 + (−18)");
        ledger.Commission.Should().Be(4.0m);
        ledger.Swap.Should().Be(3.0m);
        ledger.TotalTrades.Should().Be(2);
        ledger.WinningTrades.Should().Be(1);
        ledger.WinRatePct.Should().Be(50.0);
        ledger.MaxDrawdownPct.Should().Be(4.6);

        ledger.Trades.Should().HaveCount(2);
        ledger.Trades[0].Direction.Should().Be("Long");
        ledger.Trades[0].NetPnL.Should().Be(50.0m);
        ledger.Trades[0].OpenedAtUtc.Should().Be(new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc));
        ledger.Trades[0].ClosedAtUtc.Should().Be(new DateTime(2024, 1, 3, 11, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Empty_or_partial_report_yields_an_empty_ledger_not_a_throw()
    {
        var ledger = ShamshirReportParser.Parse("{}");
        ledger.TotalTrades.Should().Be(0);
        ledger.NetProfit.Should().Be(0m);
        ledger.Trades.Should().BeEmpty();
    }
}

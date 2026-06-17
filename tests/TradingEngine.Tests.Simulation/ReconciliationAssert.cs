using TradingEngine.Infrastructure.Persistence.Entities;

namespace TradingEngine.Tests.Simulation;

public static class ReconciliationAssert
{
    /// <summary>
    /// Verifies that the key triple-cross equality holds:
    ///   1. NetPnL (from stats summary) == Σ trade net (from trade list)
    ///   2. Σ trade net == equityCurve.end (from persisted snapshots)
    ///   3. Funnel closes count == trade count
    /// </summary>
    public static void StatsAreConsistent(
        decimal netPnL,
        IReadOnlyList<TradeResultEntity> trades,
        IReadOnlyList<EquitySnapshot>? equityCurve,
        int funnelCloses)
    {
        var tradeNetSum = trades.Sum(t => t.NetPnLAmount);

        tradeNetSum.Should().Be(netPnL,
            $"NetPnL ({netPnL}) should equal Σ trade NetPnLAmount ({tradeNetSum})");

        if (equityCurve is { Count: > 0 })
        {
            var equityEnd = equityCurve[^1].Equity;
            var startEquity = equityCurve[0].Equity;
            var impliedPnL = equityEnd - startEquity;
            impliedPnL.Should().Be(tradeNetSum,
                $"Equity curve delta ({impliedPnL}) should equal Σ trade net ({tradeNetSum})");
        }

        funnelCloses.Should().Be(trades.Count,
            $"Funnel closes ({funnelCloses}) should equal trade count ({trades.Count})");
    }

    /// <summary>
    /// Lightweight variant when equity isn't available (e.g. in-process harness tests).
    /// </summary>
    public static void NetAndFunnelAreConsistent(
        decimal netPnL,
        IReadOnlyList<TradeResultEntity> trades,
        int funnelCloses)
    {
        var tradeNetSum = trades.Sum(t => t.NetPnLAmount);
        tradeNetSum.Should().Be(netPnL,
            $"NetPnL ({netPnL}) should equal Σ trade NetPnLAmount ({tradeNetSum})");

        funnelCloses.Should().Be(trades.Count,
            $"Funnel closes ({funnelCloses}) should equal trade count ({trades.Count})");
    }
}

using TradingEngine.Web.Dtos.Bars;

namespace TradingEngine.Web.Dtos.Trades;

/// <summary>
/// iter-redesign P6.2 — trade-detail chart payload: the candlestick window around a trade plus
/// entry/exit/SL/TP markers, so the UI can render "what happened" for one trade.
/// </summary>
public sealed record TradeChartResponse
{
    public Guid TradeId { get; init; }
    public string Symbol { get; init; } = "";
    public string Timeframe { get; init; } = "H1";
    public string Direction { get; init; } = "";
    public List<BarResponse> Bars { get; init; } = [];
    public List<ChartMarker> Markers { get; init; } = [];

    // X3: the stop's journey — initial SL at entry plus every BREAKEVEN/TRAIL move journaled for
    // this position, in time order, so the chart draws the stop as it actually walked.
    public List<StopPathPoint> StopPath { get; init; } = [];
}

public sealed record ChartMarker
{
    public long Time { get; init; }          // unix seconds (aligns with BarResponse.Time)
    public decimal Price { get; init; }
    public string Kind { get; init; } = "";  // Entry | Exit | StopLoss | TakeProfit
}

public sealed record StopPathPoint
{
    public long Time { get; init; }          // unix seconds
    public decimal Price { get; init; }
    public string Kind { get; init; } = "";  // SL | BREAKEVEN | TRAIL
}

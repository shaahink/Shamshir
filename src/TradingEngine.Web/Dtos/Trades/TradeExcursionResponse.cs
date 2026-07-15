namespace TradingEngine.Web.Dtos.Trades;

/// <summary>
/// P3.5 — per-bar excursion path for one trade. ExcursionPoint maps to the domain's
/// <c>ExcursionPoint(MinutesSinceEntry, HiPips, LoPips)</c>, returned as raw JSON so the
/// UI can render a MAE/MFE path chart independently of the engine's serialization format.
/// </summary>
public sealed record TradeExcursionResponse
{
    public Guid TradeId { get; init; }
    public string PathJson { get; init; } = "";
}

namespace TradingEngine.Services.Strategy.Filters;

public sealed class AtrRangeFilter(string atrKey, double minAtr, double maxAtr) : IEntryFilter
{
    public bool Allows(MarketContext ctx)
    {
        var atr = ctx.IndicatorValues.GetValueOrDefault(atrKey);
        return atr >= minAtr && atr <= maxAtr;
    }
}

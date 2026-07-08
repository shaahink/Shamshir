namespace TradingEngine.Services.Strategy.Filters;

public sealed class SpreadVolNoTradeFilter(decimal maxSpreadPips, decimal maxAtrPips, string atrIndicatorKey, ISymbolInfoRegistry reg) : IEntryFilter
{
    public bool Allows(MarketContext ctx)
    {
        try
        {
            var sym = reg.Get(ctx.Symbol);

            var spreadPips = (ctx.LatestTick.Ask - ctx.LatestTick.Bid) / sym.PipSize;
            if (spreadPips > maxSpreadPips)
                return false;

            if (maxAtrPips > 0
                && ctx.IndicatorValues.TryGetValue(atrIndicatorKey, out var atrPips)
                && (decimal)atrPips > maxAtrPips)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return true;
        }
    }
}

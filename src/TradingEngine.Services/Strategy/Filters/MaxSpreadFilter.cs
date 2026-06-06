namespace TradingEngine.Services.Strategy.Filters;

public sealed class MaxSpreadFilter(decimal maxPips, ISymbolInfoRegistry reg) : IEntryFilter
{
    public bool Allows(MarketContext ctx)
    {
        try
        {
            var sym = reg.Get(ctx.Symbol);
            var spreadPips = (ctx.LatestTick.Ask - ctx.LatestTick.Bid) / sym.PipSize;
            return spreadPips <= maxPips;
        }
        catch
        {
            return true;
        }
    }
}

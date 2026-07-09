using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TradingEngine.Services.Strategy.Filters;

public sealed class SpreadVolNoTradeFilter : IEntryFilter
{
    private readonly decimal _maxSpreadPips;
    private readonly decimal _maxAtrPips;
    private readonly string _atrIndicatorKey;
    private readonly ISymbolInfoRegistry _reg;
    private readonly ILogger<SpreadVolNoTradeFilter> _logger;

    public SpreadVolNoTradeFilter(decimal maxSpreadPips, decimal maxAtrPips, string atrIndicatorKey, ISymbolInfoRegistry reg, ILogger<SpreadVolNoTradeFilter>? logger = null)
    {
        _maxSpreadPips = maxSpreadPips;
        _maxAtrPips = maxAtrPips;
        _atrIndicatorKey = atrIndicatorKey;
        _reg = reg;
        _logger = logger ?? NullLogger<SpreadVolNoTradeFilter>.Instance;
    }

    public bool Allows(MarketContext ctx)
    {
        try
        {
            var sym = _reg.Get(ctx.Symbol);

            var spreadPips = (ctx.LatestTick.Ask - ctx.LatestTick.Bid) / sym.PipSize;
            if (spreadPips > _maxSpreadPips)
                return false;

            if (_maxAtrPips > 0
                && ctx.IndicatorValues.TryGetValue(_atrIndicatorKey, out var atrPips)
                && (decimal)atrPips > _maxAtrPips)
            {
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SpreadVolNoTradeFilter error for {Symbol} — allowing trade (filter is fail-open)", ctx.Symbol);
            return true;
        }
    }
}

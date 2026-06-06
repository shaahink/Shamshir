using Microsoft.Extensions.Logging;
using TradingEngine.Services.SLTPCalculation;

namespace TradingEngine.Strategies.TrendBreakout;

[StrategyId("trend-breakout")]
public sealed class TrendBreakoutStrategy : IStrategy
{
    private readonly TrendBreakoutConfig _config;
    private readonly ILogger<TrendBreakoutStrategy> _logger;
    private readonly IIndicatorService _indicators;
    private int? _lastSignalDirection;
    private int _winStreak;
    private int _lossStreak;

    public TrendBreakoutParameters GetParameters() => _config.Parameters;

    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [Timeframe.H1];
    public int RequiredBarCount => Math.Max(
        Math.Max(_config.Parameters.LookbackBars, _config.Parameters.MaPeriod),
        _config.Parameters.AtrPeriod) + 5;

    public TrendBreakoutStrategy(TrendBreakoutConfig config, IIndicatorService indicators, ILogger<TrendBreakoutStrategy> logger)
    {
        _config = config;
        _indicators = indicators;
        _logger = logger;
    }

    public TradeIntent? Evaluate(MarketContext context)
    {
        try
        {
            var h1Bars = context.Bars.GetValueOrDefault(Timeframe.H1);
            if (h1Bars is null || h1Bars.Count < RequiredBarCount)
                return null;

            var latestBar = h1Bars[^1];
            var latestTick = context.LatestTick;
            var p = _config.Parameters;

            var atr = _indicators.Atr(h1Bars, p.AtrPeriod);
            var ema = _indicators.Ema(h1Bars, p.MaPeriod);

            if (atr <= 0 || ema <= 0)
                return null;

            var priorBars = h1Bars.TakeLast(p.LookbackBars + 1).SkipLast(1).ToList();
            var highestHigh = priorBars.Count > 0 ? priorBars.Max(b => b.High) : h1Bars[^1].High;
            var lowestLow = priorBars.Count > 0 ? priorBars.Min(b => b.Low) : h1Bars[^1].Low;

            var currentPrice = latestTick.Mid;
            var entryPrice = new Price(currentPrice);
            var entryDirection = (TradeDirection?)null;

            if (latestBar.High > highestHigh && currentPrice > (decimal)ema)
            {
                entryDirection = TradeDirection.Long;
            }
            else if (latestBar.Low < lowestLow && currentPrice < (decimal)ema)
            {
                entryDirection = TradeDirection.Short;
            }

            if (entryDirection is null)
                return null;

            _lastSignalDirection = entryDirection == TradeDirection.Long ? 1 : -1;

            var sl = SlTpHelpers.AtrBased(
                entryPrice, entryDirection.Value, atr, p.SlAtrMultiple,
                new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
                    0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));

            var tp = SlTpHelpers.RRMultiple(
                entryPrice, sl, entryDirection.Value, p.TpRrMultiple,
                new SymbolInfo(Symbol.Parse("EURUSD"), SymbolCategory.Forex, "EUR", "USD",
                    0.0001m, 0.00001m, 100_000, 0.01m, 100m, 0.01m, 0.03333m, 0.0001m));

            var reason = entryDirection == TradeDirection.Long
                ? $"Break of {p.LookbackBars}-bar high {highestHigh}, above EMA{p.MaPeriod}"
                : $"Break of {p.LookbackBars}-bar low {lowestLow}, below EMA{p.MaPeriod}";

            return new TradeIntent(
                context.Symbol,
                entryDirection.Value,
                OrderType.Market,
                null,
                sl,
                tp,
                Id,
                _config.RiskProfileId,
                reason,
                context.EngineTimeUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TrendBreakoutStrategy.Evaluate failed. StrategyId={StrategyId}", Id);
            return null;
        }
    }

    public void OnTradeResult(TradeResult result)
    {
        if (result.NetPnL.Amount > 0)
        {
            _winStreak++;
            _lossStreak = 0;
        }
        else
        {
            _lossStreak++;
            _winStreak = 0;
        }
    }

    public void Reset()
    {
        _lastSignalDirection = null;
        _winStreak = 0;
        _lossStreak = 0;
    }
}

using TradingEngine.Services.SLTPCalculation;

namespace TradingEngine.Strategies.TrendBreakout;

[StrategyId("trend-breakout")]
public sealed class TrendBreakoutStrategy : IStrategy
{
    private readonly TrendBreakoutConfig _config;
    private readonly ILogger<TrendBreakoutStrategy> _logger;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly Timeframe _timeframe;
    private int? _lastSignalDirection;
    private int _winStreak;
    private int _lossStreak;

    public TrendBreakoutParameters GetParameters() => _config.Parameters;

    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [_timeframe];
    public int RequiredBarCount => Math.Max(
        Math.Max(_config.Parameters.LookbackBars, _config.Parameters.MaPeriod),
        _config.Parameters.AtrPeriod) + 5;

    public IReadOnlyList<IndicatorRequest> RequiredIndicators =>
    [
        new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod),
        new($"EMA_{_config.Parameters.MaPeriod}", IndicatorType.Ema, _config.Parameters.MaPeriod),
    ];

    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public StrategyStats Stats => new(_winStreak, _lossStreak, 0, 0);

    public TrendBreakoutStrategy(
        TrendBreakoutConfig config,
        ISymbolInfoRegistry symbolRegistry,
        ILogger<TrendBreakoutStrategy> logger)
    {
        _config = config;
        _symbolRegistry = symbolRegistry;
        _logger = logger;
        _timeframe = config.Timeframe;
    }

    public TradeIntent? Evaluate(MarketContext context)
    {
        try
        {
            if (!_config.Symbols.Contains(context.Symbol.Value))
                return null;

            var h1Bars = context.Bars.GetValueOrDefault(_timeframe);
            if (h1Bars is null || h1Bars.Count < RequiredBarCount)
                return null;

            var latestBar = h1Bars[^1];
            var p = _config.Parameters;

            var atr = context.IndicatorValues.GetValueOrDefault($"ATR_{p.AtrPeriod}");
            var ema = context.IndicatorValues.GetValueOrDefault($"EMA_{p.MaPeriod}");

            if (atr <= 0 || ema <= 0)
                return null;

            var priorBars = h1Bars.TakeLast(p.LookbackBars + 1).SkipLast(1).ToList();
            var highestHigh = priorBars.Count > 0 ? priorBars.Max(b => b.High) : latestBar.High;
            var lowestLow = priorBars.Count > 0 ? priorBars.Min(b => b.Low) : latestBar.Low;

            var currentPrice = context.LatestTick.Mid;
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

            var symbolInfo = _symbolRegistry.Get(context.Symbol);
            var sl = SlTpHelpers.AtrBased(entryPrice, entryDirection.Value, atr, p.SlAtrMultiple, symbolInfo);
            var tp = SlTpHelpers.RRMultiple(entryPrice, sl, entryDirection.Value, p.TpRrMultiple, symbolInfo);

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

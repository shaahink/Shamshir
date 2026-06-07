using Microsoft.Extensions.Logging;

namespace TradingEngine.Strategies.SessionBreakout;

public sealed class SessionBreakoutStrategy : IStrategy
{
    private readonly SessionBreakoutConfig _config;
    private readonly ILogger<SessionBreakoutStrategy> _logger;
    private decimal? _rangeHigh;
    private decimal? _rangeLow;

    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [_config.Timeframe];
    public int RequiredBarCount => _config.Parameters.AtrPeriod + 5;
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public StrategyStats Stats { get; private set; } = new(0, 0, 0, 0);

    public IReadOnlyList<IndicatorRequest> RequiredIndicators =>
    [
        new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod),
    ];

    public SessionBreakoutStrategy(SessionBreakoutConfig config, ILogger<SessionBreakoutStrategy> logger)
    {
        _config = config;
        _logger = logger;
    }

    public TradeIntent? Evaluate(MarketContext context)
    {
        try
        {
            if (!_config.Symbols.Contains(context.Symbol.Value))
            {
                _logger.LogTrace("SKIP|{Id}|SymbolNotInConfig|{Sym}", Id, context.Symbol.Value);
                return null;
            }

            var h1Bars = context.Bars.GetValueOrDefault(_config.Timeframe);
            if (h1Bars is null || h1Bars.Count < RequiredBarCount)
            {
                _logger.LogTrace("SKIP|{Id}|NotEnoughBars|has={Count} needs={Need}", Id, h1Bars?.Count ?? 0, RequiredBarCount);
                return null;
            }

            var p = _config.Parameters;
            var now = TimeOnly.FromDateTime(context.EngineTimeUtc);

            if (now >= p.RangeStartUtc && now < p.RangeEndUtc)
            {
                _rangeHigh = h1Bars.Max(b => b.High);
                _rangeLow = h1Bars.Min(b => b.Low);
                return null;
            }

            if (now < p.RangeEndUtc || now >= p.EntryWindowEndUtc || _rangeHigh is null || _rangeLow is null)
                return null;

            var atr = context.IndicatorValues.GetValueOrDefault($"ATR_{p.AtrPeriod}");
            if (atr <= 0) return null;

            var currentPrice = context.LatestTick.Mid;
            var entryPrice = new Price(currentPrice);
            TradeDirection? dir = null;

            if (currentPrice > _rangeHigh.Value)
                dir = TradeDirection.Long;
            else if (currentPrice < _rangeLow.Value)
                dir = TradeDirection.Short;

            if (dir is null) return null;

            var slOffset = (decimal)(atr * p.SlAtrMultiple);
            var sl = dir == TradeDirection.Long
                ? new Price(entryPrice.Value - slOffset)
                : new Price(entryPrice.Value + slOffset);
            var tpDist = Math.Abs(entryPrice.Value - sl.Value) * (decimal)p.TpRrMultiple;
            var tp = dir == TradeDirection.Long
                ? new Price(entryPrice.Value + tpDist)
                : new Price(entryPrice.Value - tpDist);

            return new TradeIntent(context.Symbol, dir.Value, OrderType.Market, null, sl, tp,
                Id, _config.RiskProfileId,
                $"Session breakout: range=[{_rangeLow:F5}, {_rangeHigh:F5}]",
                context.EngineTimeUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SessionBreakoutStrategy.Evaluate failed. StrategyId={StrategyId}", Id);
            return null;
        }
    }

    public void OnTradeResult(TradeResult result)
    {
        var w = Stats.ConsecutiveWins; var l = Stats.ConsecutiveLosses;
        if (result.NetPnL.Amount > 0) { w++; l = 0; } else { l++; w = 0; }
        Stats = new StrategyStats(w, l, Stats.WinRateLast20, Stats.AvgRLast20);
    }

    public void Reset()
    {
        _rangeHigh = null;
        _rangeLow = null;
        Stats = new StrategyStats(0, 0, 0, 0);
    }
}

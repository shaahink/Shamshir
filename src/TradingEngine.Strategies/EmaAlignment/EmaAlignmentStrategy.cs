using Microsoft.Extensions.Logging;
using TradingEngine.Services.Strategy;

namespace TradingEngine.Strategies.EmaAlignment;

public sealed class EmaAlignmentStrategy : IStrategy
{
    private readonly EmaAlignmentConfig _config;
    private readonly ILogger<EmaAlignmentStrategy> _logger;
    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [_config.Timeframe];
    public int RequiredBarCount => Math.Max(_config.Parameters.SlowPeriod, _config.Parameters.AtrPeriod) + 5;
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public StrategyStats Stats { get; private set; } = new(0, 0, 0, 0);

    public IReadOnlyList<IndicatorRequest> RequiredIndicators =>
    [
        new($"EMA_{_config.Parameters.FastPeriod}", IndicatorType.Ema, _config.Parameters.FastPeriod),
        new($"EMA_{_config.Parameters.SlowPeriod}", IndicatorType.Ema, _config.Parameters.SlowPeriod),
        new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod),
    ];

    public EmaAlignmentStrategy(EmaAlignmentConfig config, ILogger<EmaAlignmentStrategy> logger)
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
            var fastEma = context.IndicatorValues.GetValueOrDefault($"EMA_{p.FastPeriod}");
            var slowEma = context.IndicatorValues.GetValueOrDefault($"EMA_{p.SlowPeriod}");
            var atr = context.IndicatorValues.GetValueOrDefault($"ATR_{p.AtrPeriod}");

            if (fastEma <= 0 || slowEma <= 0 || atr <= 0)
            {
                _logger.LogTrace("SKIP|{Id}|ZeroIndicators|fe={F} se={S} atr={A}", Id, fastEma, slowEma, atr);
                return null;
            }

            var currentPrice = context.LatestTick.Mid;
            TradeDirection? dir = null;

            if (fastEma > slowEma && currentPrice > (decimal)fastEma)
                dir = TradeDirection.Long;
            else if (fastEma < slowEma && currentPrice < (decimal)fastEma)
                dir = TradeDirection.Short;

            if (dir is null) return null;

            var entryPrice = new Price(currentPrice);
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
                $"EMA crossover: fast={fastEma:F5} slow={slowEma:F5}",
                context.EngineTimeUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EmaAlignmentStrategy.Evaluate failed. StrategyId={StrategyId}", Id);
            return null;
        }
    }

    public void OnTradeResult(TradeResult result)
    {
        var w = Stats.ConsecutiveWins;
        var l = Stats.ConsecutiveLosses;
        if (result.NetPnL.Amount > 0) { w++; l = 0; } else { l++; w = 0; }
        Stats = new StrategyStats(w, l, Stats.WinRateLast20, Stats.AvgRLast20);
    }

    public void Reset() => Stats = new StrategyStats(0, 0, 0, 0);
}

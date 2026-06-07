using Microsoft.Extensions.Logging;

namespace TradingEngine.Strategies.MeanReversion;

public sealed class MeanReversionStrategy : IStrategy
{
    private readonly MeanReversionConfig _config;
    private readonly ILogger<MeanReversionStrategy> _logger;
    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [_config.Timeframe];
    public int RequiredBarCount => Math.Max(_config.Parameters.BbPeriod, _config.Parameters.AtrPeriod) + 5;
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public StrategyStats Stats { get; private set; } = new(0, 0, 0, 0);

    public IReadOnlyList<IndicatorRequest> RequiredIndicators =>
    [
        new($"RSI_{_config.Parameters.RsiPeriod}", IndicatorType.Rsi, _config.Parameters.RsiPeriod),
        new($"BB_{_config.Parameters.BbPeriod}_{_config.Parameters.BbStdDev}", IndicatorType.BollingerBands, _config.Parameters.BbPeriod, _config.Parameters.BbStdDev),
        new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod),
    ];

    public MeanReversionStrategy(MeanReversionConfig config, ILogger<MeanReversionStrategy> logger)
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
            var rsi = context.IndicatorValues.GetValueOrDefault($"RSI_{p.RsiPeriod}");
            var atr = context.IndicatorValues.GetValueOrDefault($"ATR_{p.AtrPeriod}");
            if (rsi <= 0 || atr <= 0) return null;

            var currentPrice = context.LatestTick.Mid;
            var latestBar = h1Bars[^1];
            TradeDirection? dir = null;

            if (rsi < 30 && latestBar.Low <= currentPrice)
                dir = TradeDirection.Long;
            else if (rsi > 70 && latestBar.High >= currentPrice)
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
                $"Mean reversion: RSI={rsi:F1}",
                context.EngineTimeUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MeanReversionStrategy.Evaluate failed. StrategyId={StrategyId}", Id);
            return null;
        }
    }

    public void OnTradeResult(TradeResult result)
    {
        var w = Stats.ConsecutiveWins; var l = Stats.ConsecutiveLosses;
        if (result.NetPnL.Amount > 0) { w++; l = 0; } else { l++; w = 0; }
        Stats = new StrategyStats(w, l, Stats.WinRateLast20, Stats.AvgRLast20);
    }

    public void Reset() => Stats = new StrategyStats(0, 0, 0, 0);
}

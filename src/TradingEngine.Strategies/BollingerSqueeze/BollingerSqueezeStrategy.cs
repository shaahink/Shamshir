using TradingEngine.Services.SLTPCalculation;

namespace TradingEngine.Strategies.BollingerSqueeze;

[StrategyId("bb-squeeze")]
public sealed class BollingerSqueezeStrategy : IStrategy
{
    private readonly BollingerSqueezeConfig _config;
    private readonly ILogger<BollingerSqueezeStrategy> _logger;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly Timeframe _timeframe;
    private readonly Queue<double> _bbWidthQueue = new();
    private int _cooldownRemaining;
    private int _winStreak;
    private int _lossStreak;

    public BollingerSqueezeStrategy(
        BollingerSqueezeConfig config,
        ISymbolInfoRegistry symbolRegistry,
        ILogger<BollingerSqueezeStrategy> logger)
    {
        _config = config;
        _symbolRegistry = symbolRegistry;
        _logger = logger;
        _timeframe = config.Timeframe;
    }

    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IStrategyConfig Config => _config;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [_timeframe];
    public int RequiredBarCount => _config.Parameters.BbPeriod + _config.Parameters.AtrPeriod + 5;
    public IReadOnlyList<IndicatorRequest> RequiredIndicators =>
    [
        new($"BB_{_config.Parameters.BbPeriod}_{_config.Parameters.BbStdDev}", IndicatorType.BollingerBands, _config.Parameters.BbPeriod, _config.Parameters.BbStdDev),
        new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod),
    ];
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public StrategyStats Stats => new(_winStreak, _lossStreak, 0, 0);

    public TradeIntent? Evaluate(MarketContext context)
    {
        try
        {
            if (!_config.Symbols.Contains(context.Symbol.Value))
            {
                _logger.LogTrace("SKIP|{Id}|SymbolNotInConfig|{Sym}", Id, context.Symbol.Value);
                return null;
            }

            var bars = context.Bars.GetValueOrDefault(_timeframe);
            if (bars is null || bars.Count < RequiredBarCount)
            {
                _logger.LogTrace("SKIP|{Id}|NotEnoughBars|has={Count} needs={Need}", Id, bars?.Count ?? 0, RequiredBarCount);
                return null;
            }

            var prefix = $"{context.Symbol}:";
            var p = _config.Parameters;

            if (!context.IndicatorValues.TryGetValue($"{prefix}BB_{p.BbPeriod}_{p.BbStdDev}", out var middleBand))
                return null;
            if (!context.IndicatorValues.TryGetValue($"{prefix}BB_{p.BbPeriod}_{p.BbStdDev}_Upper", out var upperBand))
                return null;
            if (!context.IndicatorValues.TryGetValue($"{prefix}BB_{p.BbPeriod}_{p.BbStdDev}_Lower", out var lowerBand))
                return null;
            if (!context.IndicatorValues.TryGetValue($"{prefix}ATR_{p.AtrPeriod}", out var atr))
                return null;

            if (middleBand <= 0 || upperBand <= lowerBand || atr <= 0)
            {
                _logger.LogTrace("SKIP|{Id}|InvalidIndicators", Id);
                return null;
            }

            var bbWidth = (upperBand - lowerBand) / middleBand;
            _bbWidthQueue.Enqueue(bbWidth);
            while (_bbWidthQueue.Count > p.BbPeriod)
                _bbWidthQueue.Dequeue();

            if (_bbWidthQueue.Count < p.BbPeriod / 2)
                return null;

            var minBbbWidth = _bbWidthQueue.Min();
            var isSqueezing = bbWidth < p.SqueezeThreshold * minBbbWidth;

            if (_cooldownRemaining > 0)
            {
                _cooldownRemaining--;
                _logger.LogTrace("SKIP|{Id}|Cooldown|remaining={C}", Id, _cooldownRemaining);
                return null;
            }

            if (!isSqueezing)
                return null;

            var latestBar = bars[^1];
            var close = (double)latestBar.Close;

            TradeDirection? direction = null;
            Price sl;
            double slOffset = p.SlBandBuffer * atr;

            if (close > upperBand)
            {
                direction = TradeDirection.Long;
                sl = new Price((decimal)(lowerBand - slOffset));
            }
            else if (close < lowerBand)
            {
                direction = TradeDirection.Short;
                sl = new Price((decimal)(upperBand + slOffset));
            }
            else
            {
                return null;
            }

            _cooldownRemaining = p.CooldownBars;

            var entryPrice = new Price(context.LatestTick.Mid);
            var symbolInfo = _symbolRegistry.Get(context.Symbol);
            var tp = SlTpHelpers.RRMultiple(entryPrice, sl, direction.Value, p.TpRrMultiple, symbolInfo);

            var reason = direction == TradeDirection.Long
                ? $"BB squeeze breakout (long): close={close:F5} > upper={upperBand:F5}, width={bbWidth:F6}"
                : $"BB squeeze breakout (short): close={close:F5} < lower={lowerBand:F5}, width={bbWidth:F6}";

            return new TradeIntent(
                context.Symbol,
                direction.Value,
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
            _logger.LogError(ex, "BollingerSqueezeStrategy.Evaluate failed. StrategyId={StrategyId}", Id);
            return null;
        }
    }

    public void OnTradeResult(TradeResult result)
    {
        if (result.NetPnL.Amount > 0) { _winStreak++; _lossStreak = 0; }
        else { _lossStreak++; _winStreak = 0; }
    }

    public void Reset()
    {
        _bbWidthQueue.Clear();
        _cooldownRemaining = 0;
        _winStreak = 0;
        _lossStreak = 0;
    }
}

using TradingEngine.Services.SLTPCalculation;

namespace TradingEngine.Strategies.MacdMomentum;

[StrategyId("macd-momentum")]
public sealed class MacdMomentumStrategy : IStrategy
{
    private readonly MacdMomentumConfig _config;
    private readonly ILogger<MacdMomentumStrategy> _logger;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly Timeframe _timeframe;
    private double? _lastHist;
    private int _winStreak;
    private int _lossStreak;

    public MacdMomentumStrategy(
        MacdMomentumConfig config,
        ISymbolInfoRegistry symbolRegistry,
        ILogger<MacdMomentumStrategy> logger)
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
    public int RequiredBarCount => _config.Parameters.MacdSlow + _config.Parameters.SmaPeriod + 5;
    public IReadOnlyList<IndicatorRequest> RequiredIndicators =>
    [
        new("MACD_12_26_9", IndicatorType.Macd, 12) { Param1 = 26, Param2 = 9 },
        new($"SMA_{_config.Parameters.SmaPeriod}", IndicatorType.Sma, _config.Parameters.SmaPeriod),
        new($"ADX_{_config.Parameters.AdxPeriod}", IndicatorType.Adx, _config.Parameters.AdxPeriod),
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

            var p = _config.Parameters;

            // Indicator keys are bare (e.g. "ATR_14"), matching IndicatorSnapshotService —
            // see MarketContext.IndicatorValues. Do NOT prefix with the symbol.
            if (!context.IndicatorValues.TryGetValue($"SMA_{p.SmaPeriod}", out var sma200))
                return null;
            if (!context.IndicatorValues.TryGetValue("MACD_12_26_9_Histogram", out var histNow))
                return null;
            if (!context.IndicatorValues.TryGetValue($"ADX_{p.AdxPeriod}", out var adx))
                return null;
            if (!context.IndicatorValues.TryGetValue($"ATR_{p.AtrPeriod}", out var atr))
                return null;

            if (adx < p.AdxMinThreshold)
            {
                _logger.LogTrace("SKIP|{Id}|ADX below threshold|adx={Adx}", Id, adx);
                return null;
            }

            var latestBar = bars[^1];
            var close = (double)latestBar.Close;
            var priceAboveSma = close > sma200;

            if (_lastHist is null)
            {
                _lastHist = histNow;
                return null;
            }

            var histPrev = _lastHist.Value;
            _lastHist = histNow;

            TradeDirection? direction = null;
            string reason;

            if (histPrev < 0 && histNow >= 0 && priceAboveSma)
            {
                direction = TradeDirection.Long;
                reason = $"MACD histogram crossed above zero, price={close:F5} > SMA{p.SmaPeriod}={sma200:F5}";
            }
            else if (histPrev > 0 && histNow <= 0 && !priceAboveSma)
            {
                direction = TradeDirection.Short;
                reason = $"MACD histogram crossed below zero, price={close:F5} <= SMA{p.SmaPeriod}={sma200:F5}";
            }
            else
            {
                return null;
            }

            var entryPrice = new Price(context.LatestTick.Mid);
            var symbolInfo = _symbolRegistry.Get(context.Symbol);
            var sl = SlTpHelpers.AtrBased(entryPrice, direction.Value, atr, p.SlAtrMultiple, symbolInfo);
            var tp = SlTpHelpers.RRMultiple(entryPrice, sl, direction.Value, p.TpRrMultiple, symbolInfo);

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
            _logger.LogError(ex, "MacdMomentumStrategy.Evaluate failed. StrategyId={StrategyId}", Id);
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
        _lastHist = null;
        _winStreak = 0;
        _lossStreak = 0;
    }
}

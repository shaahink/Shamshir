using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Services.SLTPCalculation;

namespace TradingEngine.Strategies.TrendBreakout;

[StrategyId("trend-breakout")]
public sealed class TrendBreakoutStrategy : IStrategy
{
    private readonly TrendBreakoutConfig _config;
    private readonly ILogger<TrendBreakoutStrategy> _logger;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private int? _lastSignalDirection;
    private int _winStreak;
    private int _lossStreak;

    public TrendBreakoutParameters GetParameters() => _config.Parameters;

    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IStrategyConfig Config => _config;
    public Timeframe EntryTimeframe => _config.EntryTimeframe;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [_config.EntryTimeframe];
    public int RequiredBarCount => Math.Max(
        Math.Max(_config.Parameters.LookbackBars, _config.Parameters.MaPeriod),
        _config.Parameters.AtrPeriod) + 5;

    public IReadOnlyList<IndicatorRequest> RequiredIndicators =>
    [
        new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod, Timeframe: _config.EntryTimeframe),
        new($"EMA_{_config.Parameters.MaPeriod}", IndicatorType.Ema, _config.Parameters.MaPeriod, Timeframe: _config.EntryTimeframe),
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
    }

    public TradeIntent? Evaluate(MarketContext context)
    {
        try
        {
            var bars = context.Bars.GetValueOrDefault(_config.EntryTimeframe);
            if (bars is null || bars.Count < RequiredBarCount)
            {
                _logger.LogTrace("SKIP|{Id}|NotEnoughBars|has={Count} needs={Need}", Id, bars?.Count ?? 0, RequiredBarCount);
                return null;
            }

            var latestBar = bars[^1];
            var p = _config.Parameters;

            var atr = context.IndicatorValues.GetValueOrDefault($"ATR_{p.AtrPeriod}");
            var ema = context.IndicatorValues.GetValueOrDefault($"EMA_{p.MaPeriod}");

            if (atr <= 0 || ema <= 0)
            {
                _logger.LogTrace("SKIP|{Id}|ZeroIndicators|atr={Atr} ema={Ema}", Id, atr, ema);
                return null;
            }

            var priorBars = bars.TakeLast(p.LookbackBars + 1).SkipLast(1).ToList();
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
            var pm = _config.PositionManagement;
            var sl = SlTpHelpers.AtrBased(entryPrice, entryDirection.Value, atr, pm.StopLoss.AtrMultiple, symbolInfo);
            var tp = SlTpHelpers.RRMultiple(entryPrice, sl, entryDirection.Value, pm.TakeProfit.RrMultiple, symbolInfo);

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

    public static TrendBreakoutStrategy Create(StrategyConfigEntry entry, IServiceProvider sp)
    {
        var config = new TrendBreakoutConfig
        {
            Id = entry.Id,
            DisplayName = entry.DisplayName,
            Enabled = entry.Enabled,
            RiskProfileId = entry.RiskProfileId,
            RegimeFilter = entry.RegimeFilter ?? new(),
            OrderEntry = entry.OrderEntry ?? new(),
            PositionManagement = entry.PositionManagement ?? new(),
            Parameters = StrategyFactoryHelper.DeserializeParams<TrendBreakoutParameters>(entry.Parameters),
            EntryTimeframe = entry.EntryTimeframe ?? Timeframe.H1,
            Symbol = entry.Symbol,
        };
        return new TrendBreakoutStrategy(config,
            sp.GetRequiredService<ISymbolInfoRegistry>(),
            sp.GetRequiredService<ILogger<TrendBreakoutStrategy>>());
    }
}

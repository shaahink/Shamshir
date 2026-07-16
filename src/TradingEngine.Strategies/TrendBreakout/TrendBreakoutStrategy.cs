using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Services.SLTPCalculation;
using TradingEngine.Services.Strategy.Filters;

namespace TradingEngine.Strategies.TrendBreakout;

[StrategyId("trend-breakout")]
public sealed class TrendBreakoutStrategy : IStrategy
{
    private readonly TrendBreakoutConfig _config;
    private readonly ILogger<TrendBreakoutStrategy> _logger;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly IReadOnlyList<IEntryFilter> _entryFilters;
    private int? _lastSignalDirection;
    private int _cooldownRemaining;
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
        ILogger<TrendBreakoutStrategy> logger,
        IReadOnlyList<IEntryFilter>? entryFilters = null)
    {
        _config = config;
        _symbolRegistry = symbolRegistry;
        _logger = logger;
        _entryFilters = entryFilters ?? [];
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

            foreach (var filter in _entryFilters)
            {
                if (!filter.Allows(context))
                {
                    _logger.LogTrace("SKIP|{Id}|EntryFilterBlocked|filter={FilterType}", Id, filter.GetType().Name);
                    return null;
                }
            }

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

            if (_cooldownRemaining > 0) _cooldownRemaining--;

            // P2.3/D5: single-fire — a monotonic trend makes EVERY bar a "fresh" N-bar high/low under the
            // naive check above, re-firing every bar. Only fire when the PRIOR bar was NOT itself already
            // breaking ITS OWN rolling window (a false→true transition), i.e. this is genuinely the FIRST
            // breakout bar of the run, not a continuation. Also gated by a cooldown after any fire.
            var priorBar = bars[^2];
            var priorWindowBars = bars.Count >= p.LookbackBars + 2
                ? bars.Skip(bars.Count - p.LookbackBars - 2).Take(p.LookbackBars).ToList()
                : [];
            var priorHighestHigh = priorWindowBars.Count > 0 ? priorWindowBars.Max(b => b.High) : priorBar.High;
            var priorLowestLow = priorWindowBars.Count > 0 ? priorWindowBars.Min(b => b.Low) : priorBar.Low;
            var wasPriorBarBreakoutUp = priorBar.High > priorHighestHigh;
            var wasPriorBarBreakoutDown = priorBar.Low < priorLowestLow;

            if (_cooldownRemaining <= 0 && latestBar.High > highestHigh && currentPrice > (decimal)ema && !wasPriorBarBreakoutUp)
            {
                entryDirection = TradeDirection.Long;
            }
            else if (_cooldownRemaining <= 0 && latestBar.Low < lowestLow && currentPrice < (decimal)ema && !wasPriorBarBreakoutDown)
            {
                entryDirection = TradeDirection.Short;
            }

            if (entryDirection is null)
                return null;

            _lastSignalDirection = entryDirection == TradeDirection.Long ? 1 : -1;
            _cooldownRemaining = p.CooldownBars;

            var symbolInfo = _symbolRegistry.Get(context.Symbol);
            var pm = _config.PositionManagement;
            var sl = SlTpHelpers.AtrBased(entryPrice, entryDirection.Value, atr, pm.StopLoss.AtrMultiple, symbolInfo);
            var tp = SlTpHelpers.TakeProfitFor(pm.TakeProfit, entryPrice, sl, entryDirection.Value, atr, symbolInfo);

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
        _cooldownRemaining = 0;
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
            EntryFilter = entry.EntryFilter,
            Parameters = StrategyFactoryHelper.DeserializeParams<TrendBreakoutParameters>(entry.Parameters),
            EntryTimeframe = entry.EntryTimeframe ?? Timeframe.H1,
            Symbol = entry.Symbol,
        };

        var entryFilters = new List<IEntryFilter>();
        if (entry.EntryFilter is { Enabled: true })
        {
            var reg = sp.GetRequiredService<ISymbolInfoRegistry>();
            entryFilters.Add(new SpreadVolNoTradeFilter(
                entry.EntryFilter.MaxSpreadPips,
                entry.EntryFilter.MaxAtrPips,
                entry.EntryFilter.AtrIndicatorKey,
                reg));
        }

        return new TrendBreakoutStrategy(config,
            sp.GetRequiredService<ISymbolInfoRegistry>(),
            sp.GetRequiredService<ILogger<TrendBreakoutStrategy>>(),
            entryFilters);
    }
}

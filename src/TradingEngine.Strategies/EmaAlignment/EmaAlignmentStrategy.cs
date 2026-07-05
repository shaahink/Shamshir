using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TradingEngine.Services.Strategy;

namespace TradingEngine.Strategies.EmaAlignment;

[StrategyId("ema-alignment")]
public sealed class EmaAlignmentStrategy : IStrategy
{
    private readonly EmaAlignmentConfig _config;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly ILogger<EmaAlignmentStrategy> _logger;
    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IStrategyConfig Config => _config;
    public Timeframe EntryTimeframe => _config.EntryTimeframe;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [_config.EntryTimeframe];
    public int RequiredBarCount => Math.Max(_config.Parameters.SlowPeriod, _config.Parameters.AtrPeriod) + _config.Parameters.CrossoverLookback + 5;
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public StrategyStats Stats { get; private set; } = new(0, 0, 0, 0);

    public IReadOnlyList<IndicatorRequest> RequiredIndicators =>
    [
        new($"EMA_{_config.Parameters.FastPeriod}", IndicatorType.Ema, _config.Parameters.FastPeriod, Timeframe: _config.EntryTimeframe),
        new($"EMA_{_config.Parameters.SlowPeriod}", IndicatorType.Ema, _config.Parameters.SlowPeriod, Timeframe: _config.EntryTimeframe),
        new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod, Timeframe: _config.EntryTimeframe),
    ];

    public EmaAlignmentStrategy(EmaAlignmentConfig config, ISymbolInfoRegistry symbolRegistry, ILogger<EmaAlignmentStrategy> logger)
    {
        _config = config;
        _symbolRegistry = symbolRegistry;
        _logger = logger;
    }

    /// <summary>
    /// P2.3/D5: real edge, replacing the old state CONDITION (fast&gt;slow AND price&gt;fast — true every
    /// bar of any trend, despite the comment claiming "crossover"). Requires: (1) a fast/slow EMA crossover
    /// within <see cref="EmaAlignmentParameters.CrossoverLookback"/> bars, (2) no earlier bar since that
    /// crossover has touched the fast EMA, (3) THIS bar touches the fast EMA (the pullback) and closes back
    /// on the trend side of it (confirmation). Fully derived from bars + the EMA series (P2.1) — no private
    /// state needed, so replay of the same tape always gives the same answer regardless of call cadence.
    /// </summary>
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

            var p = _config.Parameters;
            var atr = context.IndicatorValues.GetValueOrDefault($"ATR_{p.AtrPeriod}");
            if (atr <= 0) return null;

            var fastSeries = context.GetSeries($"EMA_{p.FastPeriod}");
            var slowSeries = context.GetSeries($"EMA_{p.SlowPeriod}");
            var windowSize = Math.Min(bars.Count, Math.Min(fastSeries.Count, Math.Min(slowSeries.Count, p.CrossoverLookback)));
            if (windowSize < 3) return null; // need room for a cross, a gap, and the current bar

            var barsWindow = bars.TakeLast(windowSize).ToList();
            var fastWindow = fastSeries.TakeLast(windowSize).ToList();
            var slowWindow = slowSeries.TakeLast(windowSize).ToList();

            // Most recent crossover strictly before "now" (index windowSize-1).
            var crossIdx = -1;
            TradeDirection? crossDir = null;
            for (var idx = windowSize - 2; idx >= 1; idx--)
            {
                if (fastWindow[idx] > slowWindow[idx] && fastWindow[idx - 1] <= slowWindow[idx - 1])
                {
                    crossIdx = idx; crossDir = TradeDirection.Long; break;
                }
                if (fastWindow[idx] < slowWindow[idx] && fastWindow[idx - 1] >= slowWindow[idx - 1])
                {
                    crossIdx = idx; crossDir = TradeDirection.Short; break;
                }
            }
            if (crossIdx < 0 || crossDir is null) return null;

            // No earlier touch of the fast EMA between the crossover and now (exclusive of both ends) —
            // otherwise this is a SECOND (or later) pullback, not the first.
            for (var idx = crossIdx + 1; idx < windowSize - 1; idx++)
            {
                if ((double)barsWindow[idx].Low <= fastWindow[idx] && (double)barsWindow[idx].High >= fastWindow[idx])
                    return null;
            }

            var latestBar = barsWindow[^1];
            var currentFast = fastWindow[^1];
            var touchesNow = (double)latestBar.Low <= currentFast && (double)latestBar.High >= currentFast;
            if (!touchesNow) return null;

            var close = (double)latestBar.Close;
            if (crossDir == TradeDirection.Long && close <= currentFast) return null;
            if (crossDir == TradeDirection.Short && close >= currentFast) return null;

            var resolver = new SlTpResolver();
            var entryPrice = new Price(context.LatestTick.Mid);
            var symbolInfo = _symbolRegistry.Get(context.Symbol);
            var (sl, tp) = resolver.Resolve(entryPrice, crossDir.Value, atr, symbolInfo, _config.PositionManagement);

            return new TradeIntent(context.Symbol, crossDir.Value, OrderType.Market, null, sl, tp,
                Id, _config.RiskProfileId,
                $"EMA{p.FastPeriod}/{p.SlowPeriod} {crossDir} crossover {windowSize - 1 - crossIdx} bars ago, first pullback touch of EMA{p.FastPeriod}",
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

    public static EmaAlignmentStrategy Create(StrategyConfigEntry entry, IServiceProvider sp)
    {
        var config = new EmaAlignmentConfig(
            entry.Id, entry.DisplayName, entry.Enabled,
            entry.RiskProfileId,
            StrategyFactoryHelper.DeserializeParams<EmaAlignmentParameters>(entry.Parameters))
        {
            RegimeFilter = entry.RegimeFilter ?? new(),
            OrderEntry = entry.OrderEntry ?? new(),
            PositionManagement = entry.PositionManagement ?? new(),
            EntryTimeframe = entry.EntryTimeframe ?? Timeframe.H1,
            Symbol = entry.Symbol,
        };
        return new EmaAlignmentStrategy(config,
            sp.GetRequiredService<ISymbolInfoRegistry>(),
            sp.GetRequiredService<ILogger<EmaAlignmentStrategy>>());
    }
}

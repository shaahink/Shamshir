using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingEngine.Strategies.MeanReversion;

[StrategyId("mean-reversion")]
public sealed class MeanReversionStrategy : IStrategy
{
    private readonly MeanReversionConfig _config;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly ILogger<MeanReversionStrategy> _logger;
    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IStrategyConfig Config => _config;
    public Timeframe EntryTimeframe => _config.EntryTimeframe;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [_config.EntryTimeframe];
    public int RequiredBarCount => Math.Max(_config.Parameters.BbPeriod, _config.Parameters.AtrPeriod) + 5;
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public StrategyStats Stats { get; private set; } = new(0, 0, 0, 0);

    public IReadOnlyList<IndicatorRequest> RequiredIndicators =>
    [
        new($"RSI_{_config.Parameters.RsiPeriod}", IndicatorType.Rsi, _config.Parameters.RsiPeriod),
        new($"BB_{_config.Parameters.BbPeriod}_{_config.Parameters.BbStdDev}", IndicatorType.BollingerBands, _config.Parameters.BbPeriod, _config.Parameters.BbStdDev),
        new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod),
    ];

    public MeanReversionStrategy(MeanReversionConfig config, ISymbolInfoRegistry symbolRegistry, ILogger<MeanReversionStrategy> logger)
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

            var p = _config.Parameters;
            var rsi = context.IndicatorValues.GetValueOrDefault($"RSI_{p.RsiPeriod}");
            var atr = context.IndicatorValues.GetValueOrDefault($"ATR_{p.AtrPeriod}");
            if (rsi <= 0 || atr <= 0) return null;

            var currentPrice = context.LatestTick.Mid;
            var latestBar = bars[^1];
            TradeDirection? dir = null;

            // Close must sit in the lower (long) / upper (short) fraction of the bar's own range —
            // a rejection wick — rather than within a fixed 0.2% of price (which on H1 majors is
            // ~20+ pips and almost always satisfied, i.e. effectively no filter).
            var range = latestBar.High - latestBar.Low;
            var frac = (decimal)p.ProximityToExtremeFraction;
            var nearLow = range > 0 && (latestBar.Close - latestBar.Low) <= range * frac;
            var nearHigh = range > 0 && (latestBar.High - latestBar.Close) <= range * frac;

            if (rsi < p.RsiOversold && nearLow)
                dir = TradeDirection.Long;
            else if (rsi > p.RsiOverbought && nearHigh)
                dir = TradeDirection.Short;

            if (dir is null) return null;

            var resolver = new SlTpResolver();
            var entryPrice = new Price(currentPrice);
            var symbolInfo = _symbolRegistry.Get(context.Symbol);
            var (sl, tp) = resolver.Resolve(entryPrice, dir.Value, atr, symbolInfo, _config.PositionManagement);

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

    private readonly Queue<bool> _recentTrades = new();

    public void OnTradeResult(TradeResult result)
    {
        var w = Stats.ConsecutiveWins; var l = Stats.ConsecutiveLosses;
        if (result.NetPnL.Amount > 0) { w++; l = 0; } else { l++; w = 0; }

        _recentTrades.Enqueue(result.NetPnL.Amount > 0);
        if (_recentTrades.Count > 20) _recentTrades.Dequeue();

        var winRate20 = _recentTrades.Count > 0
            ? (double)_recentTrades.Count(t => t) / _recentTrades.Count : 0d;

        Stats = new StrategyStats(w, l, winRate20, Stats.AvgRLast20);
    }

    public void Reset() => Stats = new StrategyStats(0, 0, 0, 0);

    public static MeanReversionStrategy Create(StrategyConfigEntry entry, IServiceProvider sp)
    {
        var config = new MeanReversionConfig(
            entry.Id, entry.DisplayName, entry.Enabled,
            entry.RiskProfileId,
            StrategyFactoryHelper.DeserializeParams<MeanReversionParameters>(entry.Parameters))
        {
            RegimeFilter = entry.RegimeFilter ?? new(),
            OrderEntry = entry.OrderEntry ?? new(),
            PositionManagement = entry.PositionManagement ?? new(),
            EntryTimeframe = entry.EntryTimeframe ?? Timeframe.H1,
            Symbol = entry.Symbol,
        };
        return new MeanReversionStrategy(config,
            sp.GetRequiredService<ISymbolInfoRegistry>(),
            sp.GetRequiredService<ILogger<MeanReversionStrategy>>());
    }
}

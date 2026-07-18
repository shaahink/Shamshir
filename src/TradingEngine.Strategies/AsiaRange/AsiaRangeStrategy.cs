using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Strategies.Sessions;

namespace TradingEngine.Strategies.AsiaRange;

/// <summary>
/// V4 session family — breakout of the overnight Tokyo range at the London open. Identical in shape to
/// <c>london-orb</c>, but the range is built over the 00:00–06:00 UTC overnight window (routed through
/// <see cref="SessionWindow"/>'s wrap-aware compare so the whole family is uniform and the wrap primitive
/// is exercised), and the breakout is taken in the 07:00–10:00 UTC entry window. Flattened at 16:00 UTC.
/// </summary>
[StrategyId("asia-range")]
public sealed class AsiaRangeStrategy : IStrategy
{
    private readonly AsiaRangeConfig _config;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly ILogger<AsiaRangeStrategy> _logger;
    private readonly OpeningRangeTracker _rangeTracker;

    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IStrategyConfig Config => _config;
    public Timeframe EntryTimeframe => _config.EntryTimeframe;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [_config.EntryTimeframe];
    public int RequiredBarCount => _config.Parameters.AtrPeriod + 5;
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public StrategyStats Stats { get; private set; } = new(0, 0, 0, 0);

    public IReadOnlyList<IndicatorRequest> RequiredIndicators =>
    [
        new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod, Timeframe: _config.EntryTimeframe),
    ];

    public AsiaRangeStrategy(AsiaRangeConfig config, ISymbolInfoRegistry symbolRegistry, ILogger<AsiaRangeStrategy> logger)
    {
        _config = config;
        _symbolRegistry = symbolRegistry;
        _logger = logger;
        var p = config.Parameters;
        _rangeTracker = new OpeningRangeTracker(new SessionWindow(p.RangeStartUtc, p.RangeEndUtc));
    }

    public TradeIntent? Evaluate(MarketContext context)
    {
        try
        {
            var bars = context.Bars.GetValueOrDefault(_config.EntryTimeframe);
            if (bars is null || bars.Count < RequiredBarCount)
            {
                return null;
            }

            var p = _config.Parameters;
            var now = TimeOnly.FromDateTime(context.EngineTimeUtc);
            if (now < p.EntryWindowStartUtc || now >= p.EntryWindowEndUtc)
            {
                return null;
            }

            var range = _rangeTracker.Compute(bars, context.EngineTimeUtc);
            if (range is null)
            {
                return null;
            }

            var atr = context.IndicatorValues.GetValueOrDefault($"ATR_{p.AtrPeriod}");
            if (atr <= 0)
            {
                return null;
            }

            var price = context.LatestTick.Mid;
            TradeDirection? dir = null;
            if (price > range.Value.High)
            {
                dir = TradeDirection.Long;
            }
            else if (price < range.Value.Low)
            {
                dir = TradeDirection.Short;
            }

            if (dir is null)
            {
                return null;
            }

            var resolver = new SlTpResolver();
            var symbolInfo = _symbolRegistry.Get(context.Symbol);
            var (sl, tp) = resolver.Resolve(new Price(price), dir.Value, atr, symbolInfo, _config.PositionManagement);

            return new TradeIntent(context.Symbol, dir.Value, OrderType.Market, null, sl, tp,
                Id, _config.RiskProfileId,
                $"Asia range: range=[{range.Value.Low:F5}, {range.Value.High:F5}]",
                context.EngineTimeUtc);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AsiaRangeStrategy.Evaluate failed. StrategyId={StrategyId}", Id);
            return null;
        }
    }

    public void OnTradeResult(TradeResult result)
    {
        var w = Stats.ConsecutiveWins;
        var l = Stats.ConsecutiveLosses;
        if (result.NetPnL.Amount > 0)
        {
            w++;
            l = 0;
        }
        else
        {
            l++;
            w = 0;
        }
        Stats = new StrategyStats(w, l, Stats.WinRateLast20, Stats.AvgRLast20);
    }

    public void Reset()
    {
        _rangeTracker.Reset();
        Stats = new StrategyStats(0, 0, 0, 0);
    }

    public static AsiaRangeStrategy Create(StrategyConfigEntry entry, IServiceProvider sp)
    {
        var config = new AsiaRangeConfig(
            entry.Id, entry.DisplayName, entry.Enabled,
            entry.RiskProfileId,
            StrategyFactoryHelper.DeserializeParams<AsiaRangeParameters>(entry.Parameters))
        {
            RegimeFilter = entry.RegimeFilter ?? new(),
            OrderEntry = entry.OrderEntry ?? new(),
            PositionManagement = entry.PositionManagement ?? new(),
            EntryTimeframe = entry.EntryTimeframe ?? Timeframe.M15,
            Symbol = entry.Symbol,
        };
        return new AsiaRangeStrategy(config,
            sp.GetRequiredService<ISymbolInfoRegistry>(),
            sp.GetRequiredService<ILogger<AsiaRangeStrategy>>());
    }
}

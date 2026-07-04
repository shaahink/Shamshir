using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Services.SLTPCalculation;

namespace TradingEngine.Strategies.SuperTrend;

[StrategyId("super-trend")]
public sealed class SuperTrendStrategy : IStrategy
{
    private readonly SuperTrendConfig _config;
    private readonly ILogger<SuperTrendStrategy> _logger;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private int? _prevDirection;
    private int _winStreak;
    private int _lossStreak;

    public SuperTrendStrategy(
        SuperTrendConfig config,
        ISymbolInfoRegistry symbolRegistry,
        ILogger<SuperTrendStrategy> logger)
    {
        _config = config;
        _symbolRegistry = symbolRegistry;
        _logger = logger;
    }

    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IStrategyConfig Config => _config;
    public Timeframe EntryTimeframe => _config.EntryTimeframe;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [_config.EntryTimeframe];
    public int RequiredBarCount => Math.Max(_config.Parameters.AtrPeriod, _config.Parameters.AdxPeriod) * 2 + 5;
    public IReadOnlyList<IndicatorRequest> RequiredIndicators =>
    [
        new($"ST_{_config.Parameters.AtrPeriod}_{_config.Parameters.AtrMultiplier}", IndicatorType.SuperTrend, _config.Parameters.AtrPeriod) { Param2 = _config.Parameters.AtrMultiplier },
        new($"ADX_{_config.Parameters.AdxPeriod}", IndicatorType.Adx, _config.Parameters.AdxPeriod),
        new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod),
    ];
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public StrategyStats Stats => new(_winStreak, _lossStreak, 0, 0);

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

            // Indicator keys are bare (e.g. "ST_10_3"), matching IndicatorSnapshotService —
            // see MarketContext.IndicatorValues. Do NOT prefix with the symbol.
            if (!context.IndicatorValues.TryGetValue($"ST_{p.AtrPeriod}_{p.AtrMultiplier}", out var stLine))
                return null;
            if (!context.IndicatorValues.TryGetValue($"ST_{p.AtrPeriod}_{p.AtrMultiplier}_Direction", out var stDirRaw))
                return null;
            if (!context.IndicatorValues.TryGetValue($"ADX_{p.AdxPeriod}", out var adx))
                return null;
            context.IndicatorValues.TryGetValue($"ATR_{p.AtrPeriod}", out var atr);

            var stDir = (int)stDirRaw;
            if (stDir is not 1 and not -1)
                return null;

            if (adx < p.AdxMinThreshold)
            {
                _logger.LogTrace("SKIP|{Id}|ADX below threshold|adx={Adx}", Id, adx);
                return null;
            }

            if (_prevDirection is null)
            {
                _prevDirection = stDir;
                return null;
            }

            if (_prevDirection == stDir)
                return null;

            var flippedDir = _prevDirection;
            _prevDirection = stDir;

            TradeDirection direction = stDir == 1 ? TradeDirection.Long : TradeDirection.Short;
            var entryPrice = new Price(context.LatestTick.Mid);
            var symbolInfo = _symbolRegistry.Get(context.Symbol);
            var resolver = new SlTpResolver();
            var (sl, tp) = resolver.Resolve(entryPrice, direction, atr, symbolInfo,
                _config.PositionManagement, new Price((decimal)stLine));

            var reason = flippedDir == -1
                ? $"SuperTrend flipped bullish, direction={stDir}, ST line={stLine:F5}, ADX={adx:F2}"
                : $"SuperTrend flipped bearish, direction={stDir}, ST line={stLine:F5}, ADX={adx:F2}";

            return new TradeIntent(
                context.Symbol,
                direction,
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
            _logger.LogError(ex, "SuperTrendStrategy.Evaluate failed. StrategyId={StrategyId}", Id);
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
        _prevDirection = null;
        _winStreak = 0;
        _lossStreak = 0;
    }

    public static SuperTrendStrategy Create(StrategyConfigEntry entry, IServiceProvider sp)
    {
        var config = new SuperTrendConfig
        {
            Id = entry.Id, DisplayName = entry.DisplayName, Enabled = entry.Enabled,
            RiskProfileId = entry.RiskProfileId,
            RegimeFilter = entry.RegimeFilter ?? new(),
            OrderEntry = entry.OrderEntry ?? new(),
            PositionManagement = entry.PositionManagement ?? new(),
            Parameters = StrategyFactoryHelper.DeserializeParams<SuperTrendParameters>(entry.Parameters),
            EntryTimeframe = entry.EntryTimeframe ?? Timeframe.H1,
            Symbol = entry.Symbol,
        };
        return new SuperTrendStrategy(config,
            sp.GetRequiredService<ISymbolInfoRegistry>(),
            sp.GetRequiredService<ILogger<SuperTrendStrategy>>());
    }
}

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Services.SLTPCalculation;

namespace TradingEngine.Strategies.BollingerSqueeze;

[StrategyId("bb-squeeze")]
public sealed class BollingerSqueezeStrategy : IStrategy
{
    private readonly BollingerSqueezeConfig _config;
    private readonly ILogger<BollingerSqueezeStrategy> _logger;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private readonly Queue<double> _bbWidthQueue = new();
    private int _cooldownRemaining;
    private bool _squeezeActive;
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
    }

    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IStrategyConfig Config => _config;
    public Timeframe EntryTimeframe => _config.EntryTimeframe;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [_config.EntryTimeframe];
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
            var bars = context.Bars.GetValueOrDefault(_config.EntryTimeframe);
            if (bars is null || bars.Count < RequiredBarCount)
            {
                _logger.LogTrace("SKIP|{Id}|NotEnoughBars|has={Count} needs={Need}", Id, bars?.Count ?? 0, RequiredBarCount);
                return null;
            }

            var p = _config.Parameters;

            // Indicator keys are bare (e.g. "BB_20_2"), matching IndicatorSnapshotService —
            // see MarketContext.IndicatorValues. Do NOT prefix with the symbol.
            if (!context.IndicatorValues.TryGetValue($"BB_{p.BbPeriod}_{p.BbStdDev}", out var middleBand))
                return null;
            if (!context.IndicatorValues.TryGetValue($"BB_{p.BbPeriod}_{p.BbStdDev}_Upper", out var upperBand))
                return null;
            if (!context.IndicatorValues.TryGetValue($"BB_{p.BbPeriod}_{p.BbStdDev}_Lower", out var lowerBand))
                return null;
            if (!context.IndicatorValues.TryGetValue($"ATR_{p.AtrPeriod}", out var atr))
                return null;

            if (middleBand <= 0 || upperBand <= lowerBand || atr <= 0)
            {
                _logger.LogTrace("SKIP|{Id}|InvalidIndicators", Id);
                return null;
            }

            var bbWidth = (upperBand - lowerBand) / middleBand;

            // Squeeze = bandwidth contracted to <= SqueezeThreshold (e.g. 0.8 = 80%) of its recent
            // AVERAGE, measured over the PRIOR window (before enqueuing the current width). The old code
            // compared to Min() of a window that already included the current width, so `bbWidth < 0.8*min`
            // (min ≤ bbWidth) was mathematically impossible and the squeeze never triggered.
            var priorCount = _bbWidthQueue.Count;
            var avgPriorWidth = priorCount > 0 ? _bbWidthQueue.Average() : bbWidth;
            _bbWidthQueue.Enqueue(bbWidth);
            while (_bbWidthQueue.Count > p.BbPeriod)
                _bbWidthQueue.Dequeue();

            if (priorCount < p.BbPeriod / 2)
                return null;

            // Latch the squeeze: a contraction sets the flag, and the breakout that fires it can land on a
            // LATER bar. Requiring squeeze AND breakout on the same bar (as before) is self-contradictory —
            // the breakout bar is the one expanding the bands.
            if (bbWidth <= p.SqueezeThreshold * avgPriorWidth)
                _squeezeActive = true;

            if (_cooldownRemaining > 0)
            {
                _cooldownRemaining--;
                _logger.LogTrace("SKIP|{Id}|Cooldown|remaining={C}", Id, _cooldownRemaining);
                return null;
            }

            if (!_squeezeActive)
                return null;

            var latestBar = bars[^1];
            var close = (double)latestBar.Close;

            TradeDirection? direction = null;
            Price bandSl;
            double slOffset = p.SlBandBuffer * atr;

            if (close > upperBand)
            {
                direction = TradeDirection.Long;
                bandSl = new Price((decimal)(lowerBand - slOffset));
            }
            else if (close < lowerBand)
            {
                direction = TradeDirection.Short;
                bandSl = new Price((decimal)(upperBand + slOffset));
            }
            else
            {
                return null;
            }

            _cooldownRemaining = p.CooldownBars;
            _squeezeActive = false;

            var entryPrice = new Price(context.LatestTick.Mid);
            var symbolInfo = _symbolRegistry.Get(context.Symbol);
            var resolver = new SlTpResolver();
            var (sl, tp) = resolver.Resolve(entryPrice, direction.Value, atr, symbolInfo,
                _config.PositionManagement, bandSl);

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
        _squeezeActive = false;
        _winStreak = 0;
        _lossStreak = 0;
    }

    public static BollingerSqueezeStrategy Create(StrategyConfigEntry entry, IServiceProvider sp)
    {
        var config = new BollingerSqueezeConfig
        {
            Id = entry.Id, DisplayName = entry.DisplayName, Enabled = entry.Enabled,
            RiskProfileId = entry.RiskProfileId,
            RegimeFilter = entry.RegimeFilter ?? new(),
            OrderEntry = entry.OrderEntry ?? new(),
            PositionManagement = entry.PositionManagement ?? new(),
            Parameters = StrategyFactoryHelper.DeserializeParams<BollingerSqueezeParameters>(entry.Parameters),
            EntryTimeframe = entry.EntryTimeframe ?? Timeframe.H1,
            Symbol = entry.Symbol,
        };
        return new BollingerSqueezeStrategy(config,
            sp.GetRequiredService<ISymbolInfoRegistry>(),
            sp.GetRequiredService<ILogger<BollingerSqueezeStrategy>>());
    }
}

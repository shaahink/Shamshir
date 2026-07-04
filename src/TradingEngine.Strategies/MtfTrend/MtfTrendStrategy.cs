using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Services.SLTPCalculation;

namespace TradingEngine.Strategies.MtfTrend;

[StrategyId("mtf-trend")]
public sealed class MtfTrendStrategy : IStrategy
{
    private readonly MtfTrendConfig _config;
    private readonly ILogger<MtfTrendStrategy> _logger;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private double? _prevRsi;
    private int _winStreak;
    private int _lossStreak;

    public MtfTrendStrategy(
        MtfTrendConfig config,
        ISymbolInfoRegistry symbolRegistry,
        ILogger<MtfTrendStrategy> logger)
    {
        _config = config;
        _symbolRegistry = symbolRegistry;
        _logger = logger;
    }

    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IStrategyConfig Config => _config;
    public Timeframe EntryTimeframe => _config.EntryTimeframe;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [_config.EntryTimeframe, _config.HigherTimeframe];
    public int RequiredBarCount => _config.Parameters.EmaPeriod + _config.Parameters.RsiPeriod + _config.Parameters.SwingLookback + 5;
    public IReadOnlyList<IndicatorRequest> RequiredIndicators =>
    [
        new($"RSI_{_config.Parameters.RsiPeriod}", IndicatorType.Rsi, _config.Parameters.RsiPeriod, Timeframe: Timeframe.H1),
        new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod, Timeframe: Timeframe.H1),
        new($"EMA_{_config.Parameters.EmaPeriod}", IndicatorType.Ema, _config.Parameters.EmaPeriod, Timeframe: _config.HigherTimeframe),
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
                _logger.LogTrace("SKIP|{Id}|NotEnoughBars|tf={Tf} has={Count} needs={Need}", Id, _config.EntryTimeframe, bars?.Count ?? 0, RequiredBarCount);
                return null;
            }

            var p = _config.Parameters;

            // Indicator keys are bare (e.g. "EMA_200"), matching IndicatorSnapshotService —
            // see MarketContext.IndicatorValues. Do NOT prefix with the symbol. Note the EMA is
            // computed on the higher timeframe, so it is only present when HigherTimeframe bars are fed.
            if (!context.IndicatorValues.TryGetValue($"EMA_{p.EmaPeriod}", out var h4Ema200))
                return null;
            if (!context.IndicatorValues.TryGetValue($"RSI_{p.RsiPeriod}", out var rsi))
                return null;
            if (!context.IndicatorValues.TryGetValue($"ATR_{p.AtrPeriod}", out var atr))
                return null;

            if (atr <= 0)
                return null;

            var close = (double)bars[^1].Close;
            var h4Bullish = close > h4Ema200;

            if (_prevRsi is null)
            {
                _prevRsi = rsi;
                return null;
            }

            var prevRsi = _prevRsi.Value;
            _prevRsi = rsi;

            TradeDirection? direction = null;
            string reason;

            if (h4Bullish && prevRsi < p.RsiBullishPullback && rsi >= p.RsiBullishPullback)
            {
                direction = TradeDirection.Long;
                reason = $"H4 bullish (close={close:F5} > EMA{p.EmaPeriod}={h4Ema200:F5}), RSI crossed above {p.RsiBullishPullback} (prev={prevRsi:F2} now={rsi:F2})";
            }
            else if (!h4Bullish && prevRsi > p.RsiBearishPullback && rsi <= p.RsiBearishPullback)
            {
                direction = TradeDirection.Short;
                reason = $"H4 bearish (close={close:F5} <= EMA{p.EmaPeriod}={h4Ema200:F5}), RSI crossed below {p.RsiBearishPullback} (prev={prevRsi:F2} now={rsi:F2})";
            }
            else
            {
                return null;
            }

            var entryPrice = new Price(context.LatestTick.Mid);
            var symbolInfo = _symbolRegistry.Get(context.Symbol);

            var swingSl = SlTpHelpers.SwingBased(entryPrice, direction.Value, bars, p.SwingLookback, new Pips(0), symbolInfo);
            var resolver = new SlTpResolver();
            var (sl, tp) = resolver.Resolve(entryPrice, direction.Value, atr, symbolInfo,
                _config.PositionManagement, swingSl);

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
            _logger.LogError(ex, "MtfTrendStrategy.Evaluate failed. StrategyId={StrategyId}", Id);
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
        _prevRsi = null;
        _winStreak = 0;
        _lossStreak = 0;
    }

    public static MtfTrendStrategy Create(StrategyConfigEntry entry, IServiceProvider sp)
    {
        var config = new MtfTrendConfig
        {
            Id = entry.Id, DisplayName = entry.DisplayName, Enabled = entry.Enabled,
            RiskProfileId = entry.RiskProfileId,
            RegimeFilter = entry.RegimeFilter ?? new(),
            OrderEntry = entry.OrderEntry ?? new(),
            PositionManagement = entry.PositionManagement ?? new(),
            Parameters = StrategyFactoryHelper.DeserializeParams<MtfTrendParameters>(entry.Parameters),
            EntryTimeframe = entry.EntryTimeframe ?? Timeframe.H1,
            Symbol = entry.Symbol,
        };
        return new MtfTrendStrategy(config,
            sp.GetRequiredService<ISymbolInfoRegistry>(),
            sp.GetRequiredService<ILogger<MtfTrendStrategy>>());
    }
}

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TradingEngine.Services.Helpers;
using TradingEngine.Services.SLTPCalculation;

namespace TradingEngine.Strategies.RsiDivergence;

[StrategyId("rsi-divergence")]
public sealed class RsiDivergenceStrategy : IStrategy
{
    private readonly RsiDivergenceConfig _config;
    private readonly ISymbolInfoRegistry _symbolRegistry;
    private int _winStreak;
    private int _lossStreak;

    // One-shot guard: the OpenTimeUtc of the most recent pivot this strategy already traded, so a
    // confirmed breakout doesn't re-fire on every subsequent bar while price stays past the confirmation
    // level (the pivot pair itself doesn't change until a new one forms). Legitimate trade-state, not the
    // cadence-fragile "previous indicator value" class P2.1 targeted.
    private DateTime? _lastTradedPivotTime;

    public RsiDivergenceStrategy(RsiDivergenceConfig config, ISymbolInfoRegistry symbolRegistry, ILogger<RsiDivergenceStrategy> logger)
    {
        _config = config;
        _symbolRegistry = symbolRegistry;
    }

    public string Id => _config.Id;
    public string DisplayName => _config.DisplayName;
    public IStrategyConfig Config => _config;
    public Timeframe EntryTimeframe => _config.EntryTimeframe;
    public IReadOnlyList<Timeframe> RequiredTimeframes => [_config.EntryTimeframe];
    public int RequiredBarCount => _config.Parameters.DivergenceLookback + _config.Parameters.RsiPeriod + 5;
    public IReadOnlyList<IndicatorRequest> RequiredIndicators => [
        new($"RSI_{_config.Parameters.RsiPeriod}", IndicatorType.Rsi, _config.Parameters.RsiPeriod, Timeframe: _config.EntryTimeframe),
        new($"ATR_{_config.Parameters.AtrPeriod}", IndicatorType.Atr, _config.Parameters.AtrPeriod, Timeframe: _config.EntryTimeframe),
    ];
    public IReadOnlyList<IPositionBehavior> PositionBehaviors => [];
    public StrategyStats Stats => new(_winStreak, _lossStreak, 0, 0);

    /// <summary>
    /// P2.2: real pivot-based divergence, replacing the P0-era tautology
    /// (`rsiAtLowest = lowestIdx >= 0 ? rsi : rsi` — always the current RSI, so "divergence" was never
    /// actually tested). Bullish: the two most recent confirmed swing lows show price making a LOWER low
    /// while RSI (read from its own series at those exact bar positions — P2.1) makes a HIGHER low;
    /// entry fires once price confirms by closing above the more recent pivot's High. Bearish mirrors on
    /// swing highs / a lower RSI high / closing below the pivot's Low.
    /// </summary>
    public TradeIntent? Evaluate(MarketContext context)
    {
        try
        {
            var bars = context.Bars.GetValueOrDefault(_config.EntryTimeframe);
            if (bars is null || bars.Count < RequiredBarCount) return null;

            if (!context.IndicatorValues.TryGetValue($"ATR_{_config.Parameters.AtrPeriod}", out var atr) || atr <= 0)
                return null;

            var rsiSeries = context.GetSeries($"RSI_{_config.Parameters.RsiPeriod}");
            if (rsiSeries.Count < 2) return null;

            // Align bars and RSI series: both grow by exactly one entry per evaluated decision bar, so
            // bars[^k] and rsiSeries[^k] are always the SAME bar for k <= min(both counts) — see P2.1
            // (IndicatorSnapshotService's ring buffer and the bar list are both append-only, per-bar).
            // DivergenceLookback is the TOTAL span searched for the pivot pair — a real double-bottom/top
            // (decline, bounce, second decline) easily spans dozens of bars, so this must be generous, not
            // just `strength`-sized margin around a single point.
            var lookback = _config.Parameters.DivergenceLookback;
            var strength = _config.Parameters.PivotStrength;
            var windowSize = Math.Min(bars.Count, Math.Min(rsiSeries.Count, lookback));
            if (windowSize < strength * 2 + 3) return null; // need room for at least 2 confirmable pivots

            var barsWindow = bars.TakeLast(windowSize).ToList();
            var rsiWindow = rsiSeries.TakeLast(windowSize).ToList();

            var symbolInfo = _symbolRegistry.Get(context.Symbol);
            var pm = _config.PositionManagement;

            var bullish = TryBullishDivergence(barsWindow, rsiWindow, strength, atr, symbolInfo, pm, out var bullishIntent, out var bullishPivotTime);
            if (bullish && bullishPivotTime != _lastTradedPivotTime)
            {
                _lastTradedPivotTime = bullishPivotTime;
                return bullishIntent;
            }

            var bearish = TryBearishDivergence(barsWindow, rsiWindow, strength, atr, symbolInfo, pm, out var bearishIntent, out var bearishPivotTime);
            if (bearish && bearishPivotTime != _lastTradedPivotTime)
            {
                _lastTradedPivotTime = bearishPivotTime;
                return bearishIntent;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private bool TryBullishDivergence(
        List<Bar> barsWindow, List<double> rsiWindow, int strength, double atr,
        SymbolInfo symbolInfo, PositionManagementOptions pm,
        out TradeIntent? intent, out DateTime pivotTime)
    {
        intent = null;
        pivotTime = default;

        var swingLows = PivotFinder.FindSwingLows(barsWindow, strength);
        if (swingLows.Count < 2) return false;

        var prior = swingLows[^2];
        var recent = swingLows[^1];

        var priceLowerLow = barsWindow[recent.Index].Low < barsWindow[prior.Index].Low;
        var rsiHigherLow = rsiWindow[recent.Index] > rsiWindow[prior.Index];
        var confirmed = barsWindow[^1].Close > barsWindow[recent.Index].High;
        if (!priceLowerLow || !rsiHigherLow || !confirmed) return false;

        var entry = new Price(barsWindow[^1].Close);
        var sl = new Price(recent.Price - (decimal)(pm.StopLoss.AtrMultiple * atr));
        var tp = SlTpHelpers.RRMultiple(entry, sl, TradeDirection.Long, pm.TakeProfit.RrMultiple, symbolInfo);

        intent = new TradeIntent(symbolInfo.Symbol, TradeDirection.Long, OrderType.Market, null, sl, tp,
            _config.Id, _config.RiskProfileId,
            $"Bullish RSI divergence: price lower-low ({barsWindow[prior.Index].Low:F5}→{barsWindow[recent.Index].Low:F5}), " +
            $"RSI higher-low ({rsiWindow[prior.Index]:F2}→{rsiWindow[recent.Index]:F2}), confirmed close {barsWindow[^1].Close:F5} > pivot high {barsWindow[recent.Index].High:F5}",
            barsWindow[^1].OpenTimeUtc);
        pivotTime = barsWindow[recent.Index].OpenTimeUtc;
        return true;
    }

    private bool TryBearishDivergence(
        List<Bar> barsWindow, List<double> rsiWindow, int strength, double atr,
        SymbolInfo symbolInfo, PositionManagementOptions pm,
        out TradeIntent? intent, out DateTime pivotTime)
    {
        intent = null;
        pivotTime = default;

        var swingHighs = PivotFinder.FindSwingHighs(barsWindow, strength);
        if (swingHighs.Count < 2) return false;

        var prior = swingHighs[^2];
        var recent = swingHighs[^1];

        var priceHigherHigh = barsWindow[recent.Index].High > barsWindow[prior.Index].High;
        var rsiLowerHigh = rsiWindow[recent.Index] < rsiWindow[prior.Index];
        var confirmed = barsWindow[^1].Close < barsWindow[recent.Index].Low;
        if (!priceHigherHigh || !rsiLowerHigh || !confirmed) return false;

        var entry = new Price(barsWindow[^1].Close);
        var sl = new Price(recent.Price + (decimal)(pm.StopLoss.AtrMultiple * atr));
        var tp = SlTpHelpers.RRMultiple(entry, sl, TradeDirection.Short, pm.TakeProfit.RrMultiple, symbolInfo);

        intent = new TradeIntent(symbolInfo.Symbol, TradeDirection.Short, OrderType.Market, null, sl, tp,
            _config.Id, _config.RiskProfileId,
            $"Bearish RSI divergence: price higher-high ({barsWindow[prior.Index].High:F5}→{barsWindow[recent.Index].High:F5}), " +
            $"RSI lower-high ({rsiWindow[prior.Index]:F2}→{rsiWindow[recent.Index]:F2}), confirmed close {barsWindow[^1].Close:F5} < pivot low {barsWindow[recent.Index].Low:F5}",
            barsWindow[^1].OpenTimeUtc);
        pivotTime = barsWindow[recent.Index].OpenTimeUtc;
        return true;
    }

    public void OnTradeResult(TradeResult result)
    {
        if (result.NetPnL.Amount > 0) { _winStreak++; _lossStreak = 0; }
        else { _lossStreak++; _winStreak = 0; }
    }

    public void Reset() { _winStreak = 0; _lossStreak = 0; _lastTradedPivotTime = null; }

    public static RsiDivergenceStrategy Create(StrategyConfigEntry entry, IServiceProvider sp)
    {
        var config = new RsiDivergenceConfig
        {
            Id = entry.Id, DisplayName = entry.DisplayName, Enabled = entry.Enabled,
            RiskProfileId = entry.RiskProfileId,
            RegimeFilter = entry.RegimeFilter ?? new(),
            OrderEntry = entry.OrderEntry ?? new(),
            PositionManagement = entry.PositionManagement ?? new(),
            Parameters = StrategyFactoryHelper.DeserializeParams<RsiDivergenceParameters>(entry.Parameters),
            EntryTimeframe = entry.EntryTimeframe ?? Timeframe.H1,
            Symbol = entry.Symbol,
        };
        return new RsiDivergenceStrategy(config,
            sp.GetRequiredService<ISymbolInfoRegistry>(),
            sp.GetRequiredService<ILogger<RsiDivergenceStrategy>>());
    }
}
